using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Fuse.Core.Accounts;
using Newtonsoft.Json;

namespace Fuse.Core.Auth;

/// <summary>
/// 账号凭据存储（PRD §十一：token 本地加密、不落明文、不外发）。
/// 刷新令牌用 Windows DPAPI（当前用户作用域）加密后存 accounts.json。
/// </summary>
public sealed class AccountStore
{
    private readonly string _path;
    private readonly object _lock = new();

    /// <summary>一个已保存的账号(离线 / 微软 / 外置)。</summary>
    public sealed class AccountEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>"offline" / "msa" / "external"</summary>
        public string Type { get; set; } = "offline";
        public string DisplayName { get; set; } = "";
        public string? ApiRoot { get; set; }
        public string? UserName { get; set; }
        public string? Uuid { get; set; }
        public string? AccessToken { get; set; }
        public string? ClientToken { get; set; }
        public string? PasswordEncrypted { get; set; }
        public string? MsRefreshTokenEncrypted { get; set; }
        public DateTime LastUsedAt { get; set; } = DateTime.Now;

        /// <summary>UI 显示用的类型标签。</summary>
        [JsonIgnore]
        public string TypeLabel => Type switch
        {
            "msa" => "正版",
            "external" => "皮肤站",
            _ => "离线",
        };
    }

    public sealed class Persisted
    {
        /// <summary>"offline" 或 "msa"。</summary>
        public string ActiveType { get; set; } = "offline";
        public string OfflinePlayerName { get; set; } = "Player";
        public string? MsPlayerName { get; set; }
        public string? MsPlayerUuid { get; set; }
        /// <summary>DPAPI 加密后的 Base64 刷新令牌。</summary>
        public string? MsRefreshTokenEncrypted { get; set; }

        // ---- 外置登录(authlib-injector)账号 ----
        public string? ExtApiRoot { get; set; }
        public string? ExtUserName { get; set; }
        public string? ExtPlayerName { get; set; }
        public string? ExtUuid { get; set; }
        public string? ExtAccessToken { get; set; }
        public string? ExtClientToken { get; set; }
        /// <summary>DPAPI 加密后的登录密码(每次启动用它静默换新 token)。</summary>
        public string? ExtPasswordEncrypted { get; set; }

        /// <summary>已保存的账号列表(多账号切换),以及当前账号 id。</summary>
        public List<AccountEntry> Accounts { get; set; } = new();
        public string? CurrentAccountId { get; set; }
    }

    public AccountStore(string path)
    {
        _path = path;
    }

    /// <summary>账号列表(按最近使用排序)。</summary>
    public IReadOnlyList<AccountEntry> Accounts
        => Data.Accounts.OrderByDescending(a => a.LastUsedAt).ToList();

    /// <summary>把当前 legacy 字段落到账号列表里(多账号切换的单一事实来源)。</summary>
    private AccountEntry UpsertCurrent()
    {
        var d = Data;
        var entry = d.Accounts.FirstOrDefault(a => a.Id == d.CurrentAccountId);
        if (entry == null)
        {
            entry = new AccountEntry { Id = Guid.NewGuid().ToString("N") };
            d.Accounts.Add(entry);
            d.CurrentAccountId = entry.Id;
        }
        entry.Type = d.ActiveType;
        entry.LastUsedAt = DateTime.Now;
        switch (d.ActiveType)
        {
            case "external":
                entry.ApiRoot = d.ExtApiRoot;
                entry.UserName = d.ExtUserName;
                entry.DisplayName = d.ExtPlayerName ?? d.ExtUserName ?? "皮肤站账号";
                entry.Uuid = d.ExtUuid;
                entry.AccessToken = d.ExtAccessToken;
                entry.ClientToken = d.ExtClientToken;
                entry.PasswordEncrypted = d.ExtPasswordEncrypted;
                break;
            case "msa":
                entry.DisplayName = d.MsPlayerName ?? "正版账号";
                entry.Uuid = d.MsPlayerUuid;
                entry.MsRefreshTokenEncrypted = d.MsRefreshTokenEncrypted;
                break;
            default:
                entry.DisplayName = d.OfflinePlayerName;
                empty();
                break;
        }
        return entry;

        void empty() { }
    }

    /// <summary>切换到指定账号:把它的字段还原到 legacy 字段(启动管线只认 legacy)。</summary>
    public bool SwitchTo(string accountId)
    {
        var entry = Data.Accounts.FirstOrDefault(a => a.Id == accountId);
        if (entry == null) return false;
        Data.CurrentAccountId = entry.Id;
        Data.ActiveType = entry.Type;
        switch (entry.Type)
        {
            case "external":
                Data.ExtApiRoot = entry.ApiRoot;
                Data.ExtUserName = entry.UserName;
                Data.ExtPlayerName = entry.DisplayName;
                Data.ExtUuid = entry.Uuid;
                Data.ExtAccessToken = entry.AccessToken;
                Data.ExtClientToken = entry.ClientToken;
                Data.ExtPasswordEncrypted = entry.PasswordEncrypted;
                break;
            case "msa":
                Data.MsPlayerName = entry.DisplayName;
                Data.MsPlayerUuid = entry.Uuid;
                Data.MsRefreshTokenEncrypted = entry.MsRefreshTokenEncrypted;
                break;
            default:
                Data.OfflinePlayerName = entry.DisplayName;
                break;
        }
        entry.LastUsedAt = DateTime.Now;
        Save();
        return true;
    }

    public bool RemoveAccount(string accountId)
    {
        var entry = Data.Accounts.FirstOrDefault(a => a.Id == accountId);
        if (entry == null) return false;
        Data.Accounts.Remove(entry);

        if (Data.CurrentAccountId == accountId)
        {
            Data.CurrentAccountId = null;
            var next = Data.Accounts.OrderByDescending(a => a.LastUsedAt).FirstOrDefault();
            if (next != null)
            {
                Save();
                SwitchTo(next.Id);   // 自动切到最近使用的账号
                return true;
            }
            // 没有其它账号:回落到离线档案,避免 ActiveType 指向已删除的数据
            Data.ActiveType = "offline";
        }
        Save();
        return true;
    }

    /// <summary>旧数据迁移:没有列表时,按现有 legacy 字段补一条。</summary>
    public void MigrateLegacyIfNeeded()
    {
        if (Data.Accounts.Count == 0)
        {
            UpsertCurrent();
            Save();
            return;
        }

        // 列表已存在但当前账号不在其中(旧版本登录的外置/正版账号):补一条
        var currentInList = Data.CurrentAccountId != null
                            && Data.Accounts.Any(a => a.Id == Data.CurrentAccountId);
        if (!currentInList && Data.ActiveType != "offline")
        {
            UpsertCurrent();
            Save();
        }
    }

    public Persisted Data { get; private set; } = new();

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                    Data = JsonConvert.DeserializeObject<Persisted>(File.ReadAllText(_path)) ?? new Persisted();
            }
            catch (IOException)
            {
            }
            catch (JsonException)
            {
            }
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(Data, Formatting.Indented));
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(tmp, _path);
        }
    }

    public void SetMicrosoft(string playerName, string playerUuid, string refreshToken)
    {
        // 同名正版账号复用列表条目,登录后立刻出现在多账号列表里
        var existing = Data.Accounts.FirstOrDefault(a =>
            a.Type == "msa" && string.Equals(a.DisplayName, playerName, StringComparison.OrdinalIgnoreCase));
        Data.CurrentAccountId = existing?.Id;
        Data.MsPlayerName = playerName;
        Data.MsPlayerUuid = playerUuid;
        Data.MsRefreshTokenEncrypted = Protect(refreshToken);
        Data.ActiveType = "msa";
        UpsertCurrent();
        Save();
    }

    /// <summary>解出刷新令牌；无、为空或解密失败返回 null（需重新登录）。</summary>
    public string? GetMicrosoftRefreshToken()
    {
        var enc = Data.MsRefreshTokenEncrypted;
        if (string.IsNullOrEmpty(enc)) return null;
        try
        {
            var plain = Unprotect(enc!);
            if (string.IsNullOrWhiteSpace(plain)) return null;
            ReencryptIfLegacy(isRefreshToken: true, enc!, plain);
            return plain;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public void SignOutMicrosoft()
    {
        Data.MsPlayerName = null;
        Data.MsPlayerUuid = null;
        Data.MsRefreshTokenEncrypted = null;
        Data.ActiveType = "offline";
        Save();
    }

    /// <summary>保存/切换到一个离线账号(同名复用列表条目)。</summary>
    public void UseOffline(string playerName)
    {
        var existing = Data.Accounts.FirstOrDefault(a =>
            a.Type == "offline" && string.Equals(a.DisplayName, playerName, StringComparison.OrdinalIgnoreCase));
        Data.CurrentAccountId = existing?.Id;
        Data.ActiveType = "offline";
        Data.OfflinePlayerName = playerName;
        UpsertCurrent();
        Save();
    }

    /// <summary>保存/切换到一个外置登录账号。</summary>
    public void UseExternal(string apiRoot, string userName, string password, ExternalSession session)
    {
        // 同一站点+邮箱视为同一账号,重复登录就更新它
        var existing = Data.Accounts.FirstOrDefault(a =>
            a.Type == "external"
            && string.Equals(a.UserName, userName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.ApiRoot, apiRoot, StringComparison.OrdinalIgnoreCase));
        Data.CurrentAccountId = existing?.Id;
        Data.ActiveType = "external";
        Data.ExtApiRoot = apiRoot;
        Data.ExtUserName = userName;
        Data.ExtPasswordEncrypted = Protect(password);
        Data.ExtPlayerName = session.PlayerName;
        Data.ExtUuid = session.Uuid;
        Data.ExtAccessToken = session.AccessToken;
        Data.ExtClientToken = session.ClientToken;
        UpsertCurrent();
        Save();
    }

    /// <summary>刷新令牌后回写。</summary>
    public void UpdateExternalSession(ExternalSession session)
    {
        Data.ExtAccessToken = session.AccessToken;
        Data.ExtClientToken = session.ClientToken;
        Data.ExtPlayerName = session.PlayerName;
        Data.ExtUuid = session.Uuid;
        Save();
    }

    public string? GetExternalPassword()
    {
        if (Data.ExtPasswordEncrypted == null) return null;
        var plain = Unprotect(Data.ExtPasswordEncrypted!);
        ReencryptIfLegacy(isRefreshToken: false, Data.ExtPasswordEncrypted!, plain);
        return plain;
    }

    /// <summary>旧熵解出来的密文换新熵重存(只写对应的字段;已是新熵则跳过)。</summary>
    private void ReencryptIfLegacy(bool isRefreshToken, string base64, string plain)
    {
        try
        {
            // 先确认确实是旧熵密文:新熵密文在这里会抛 CryptographicException,直接跳过
            ProtectedData.Unprotect(Convert.FromBase64String(base64), LegacyEntropy, DataProtectionScope.CurrentUser);
            var upgraded = Protect(plain);
            if (isRefreshToken) Data.MsRefreshTokenEncrypted = upgraded;
            else Data.ExtPasswordEncrypted = upgraded;
            Save();
        }
        catch (CryptographicException)
        {
        }
    }

    // DPAPI（netstandard2.0 需 System.Security.Cryptography.ProtectedData 包；net48 原生）
    // V1.0.0 改名:熵值换成 FuseLauncher;旧熵保留作兜底,老用户升级后登录态不丢
    private static byte[] Entropy { get; } = Encoding.UTF8.GetBytes("FuseLauncher.AccountStore.v1");
    private static byte[] LegacyEntropy { get; } = Encoding.UTF8.GetBytes("MCDownloader.AccountStore.v1");

    private static string Protect(string plain)
    {
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    private static string Unprotect(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            // 旧版本(V1.0.0 之前)加密的数据:用旧熵解,返回前不重加密(调用方读一次就转存新熵)
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, LegacyEntropy, DataProtectionScope.CurrentUser));
        }
    }
}

/// <summary>正版登录的管线登录节点：静默用刷新令牌续期（FR-06 正版通道）。</summary>
public sealed class MicrosoftAuthProvider : IAuthProvider
{
    private readonly AccountStore _store;
    private readonly string? _clientId;

    public MicrosoftAuthProvider(AccountStore store, string? clientId)
    {
        _store = store;
        _clientId = clientId;
    }

    public async Task<AuthResult> GetAuthAsync(CancellationToken cancellation)
    {
        var refresh = _store.GetMicrosoftRefreshToken()
                      ?? throw new InvalidOperationException("正版登录态缺失，请重新登录微软账号");
        if (string.IsNullOrWhiteSpace(refresh))
            throw new InvalidOperationException("正版登录态损坏（刷新令牌为空），请在登录面板重新登录微软账号");
        var result = await MicrosoftAccount.LoginByRefreshTokenAsync(refresh, _clientId, cancellation).ConfigureAwait(false);
        // 续期后的刷新令牌回写，保持会话长期有效
        _store.SetMicrosoft(result.PlayerName, result.PlayerUuid.ToString(), result.RefreshToken);
        return new AuthResult(result.PlayerName, result.PlayerUuid, result.AccessToken, "msa");
    }
}
