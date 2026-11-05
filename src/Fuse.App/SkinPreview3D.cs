using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace Fuse.App;

/// <summary>
/// 3D 皮肤预览(M4):WPF Viewport3D 自绘 Steve/Alex 模型,贴 64x64 皮肤 UV,
/// 披风取 cape.png。鼠标拖拽旋转、滚轮缩放,不引第三方渲染库。
/// </summary>
public class SkinPreview3D : UserControl
{
    private const double U = 1.0 / 16.0;   // 1 像素 = 1/16 单位(Minecraft 比例)

    private readonly Viewport3D _viewport = new();
    private readonly ModelVisual3D _root = new();
    private readonly Transform3DGroup _spin = new();
    private readonly PerspectiveCamera _camera = new();
    private double _yaw = 0, _pitch = 0, _distance = 6.8;   // 模型高约 2 个单位,相机距离 3~4

    public SkinPreview3D()
    {
        _camera.FieldOfView = 40;
        _camera.NearPlaneDistance = 0.1;
        _camera.FarPlaneDistance = 40;
        _camera.UpDirection = new Vector3D(0, 1, 0);
        _spin.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), _yaw)));
        _spin.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), _pitch)));
        _root.Transform = _spin;

        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(0xB0, 0xB0, 0xB0)));
        lights.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-0.4, -0.8, 0.9)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0x50, 0x50, 0x50), new Vector3D(0.4, 0.6, -0.7)));
        _root.Children.Add(new ModelVisual3D { Content = lights });

        _viewport.Camera = _camera;
        _viewport.Children.Add(_root);
        Content = _viewport;
        ClipToBounds = true;
        // 64x64 像素画放大要保持锐利:双线性会把皮肤糊成一团
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetBitmapScalingMode(_viewport, BitmapScalingMode.NearestNeighbor);

        // 交互只做"绕身体的球面旋转":中键或左键按住拖拽;滚轮不缩放(模型大小固定)
        var dragging = false;
        var last = new Point();
        MouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle && e.ChangedButton != MouseButton.Left) return;
            dragging = true;
            last = e.GetPosition(this);
            CaptureMouse();
            e.Handled = true;
        };
        MouseUp += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle && e.ChangedButton != MouseButton.Left) return;
            dragging = false;
            ReleaseMouseCapture();
        };
        MouseMove += (_, e) =>
        {
            if (!dragging) return;
            var p = e.GetPosition(this);
            _yaw += (p.X - last.X) * 0.6;
            _pitch += (p.Y - last.Y) * 0.4;
            _pitch = Math.Max(-80, Math.Min(80, _pitch));
            last = p;
            UpdateCamera();
        };
        PreviewMouseWheel += (_, e) => { e.Handled = true; };   // 吞掉滚轮,尺寸固定
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        var yaw = Math.PI * _yaw / 180;
        var pitch = Math.PI * _pitch / 180;
        // 摄像机绕模型旋转(模型固定在原地,避免光照跟着转)
        var target = new Point3D(0, 16 * U, 0);
        var x = _distance * Math.Cos(pitch) * Math.Sin(yaw);
        var y = _distance * Math.Sin(pitch) + 16 * U;
        var z = _distance * Math.Cos(pitch) * Math.Cos(yaw);
        _camera.Position = new Point3D(target.X + x, target.Y + y, target.Z + z);
        _camera.LookDirection = target - _camera.Position;
        _camera.UpDirection = new Vector3D(0, 1, 0);
    }

    /// <summary>载入皮肤(必需)与披风(可选),重建模型。</summary>
    public void SetSkin(BitmapSource? skin, BitmapSource? cape = null)
    {
        _root.Children.Clear();
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(0xB0, 0xB0, 0xB0)));
        lights.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-0.3, -0.7, 0.9)));
        _root.Children.Add(new ModelVisual3D { Content = lights });
        if (skin == null) return;

        var slim = skin.PixelWidth >= 64 && IsSlim(skin);
        var army = (slim ? 3 : 4) * U;

        // 躯干/头/腿(基础 u,v 与 skinview3d 的 setUVs 参数一致)
        AddPart(skin, new Point3D(-4 * U, 12 * U, -2 * U), 8 * U, 12 * U, 4 * U, 16, 16);   // body
        AddPart(skin, new Point3D(-4 * U, 24 * U, -4 * U), 8 * U, 8 * U, 8 * U, 0, 0);      // head
        AddPart(skin, new Point3D(-4 * U, 0, -2 * U), 4 * U, 12 * U, 4 * U, 0, 16);         // right leg
        AddPart(skin, new Point3D(0, 0, -2 * U), 4 * U, 12 * U, 4 * U, 16, 48);             // left leg

        // 手臂(Steve 4 宽 / Alex 3 宽)
        AddPart(skin, new Point3D(-4 * U - army, 12 * U, -2 * U), army, 12 * U, 4 * U, 40, 16); // right arm
        AddPart(skin, new Point3D(4 * U, 12 * U, -2 * U), army, 12 * U, 4 * U, 32, 48);         // left arm


        if (cape != null)
            AddCape(cape, new Point3D(-5 * U, 8 * U, -3.1 * U), 10 * U, 16 * U, 0.2 * U);
    }

    /// <summary>Alex 模型:左臂宽度 3 像素(第 54 列起整列透明判断)。</summary>
    private static bool IsSlim(BitmapSource skin)
    {
        try
        {
            var converted = new FormatConvertedBitmap(skin, PixelFormats.Bgra32, null, 0);
            var w = converted.PixelWidth; var h = converted.PixelHeight;
            var stride = w * 4;
            var px = new byte[stride * h];
            converted.CopyPixels(px, stride, 0);
            // 54,20 是 Alex 左臂之外多出的那一列;该像素透明则按 slim 处理
            var idx = (20 * w + 54) * 4;
            return idx + 3 < px.Length && px[idx + 3] == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---- 几何:每面单独裁剪贴图,UV 恒为 0..1(不做整图 UV 换算,避免方向出错) ----

    private void AddPart(BitmapSource tex, Point3D origin, double w, double h, double d, int u, int v)
    {
        // skinview3d 的展开公式:top(u+d,v) bottom(u+w+d,v) left(u,v+d) front(u+d,v+d)
        // right(u+w+d,v+d) back(u+w+2d,v+d);"left" 段对应模型 -X 面
        var ww = (int)(w / U);
        var hh = (int)(h / U);
        var dd = (int)(d / U);

        AddQuad(tex, new Rect(u + dd, v, ww, dd), origin, w, h, d, FaceKind.PosY);
        AddQuad(tex, new Rect(u + ww + dd, v, ww, dd), origin, w, h, d, FaceKind.NegY);
        AddQuad(tex, new Rect(u, v + dd, dd, hh), origin, w, h, d, FaceKind.NegX);
        AddQuad(tex, new Rect(u + dd, v + dd, ww, hh), origin, w, h, d, FaceKind.PosZ);
        AddQuad(tex, new Rect(u + ww + dd, v + dd, dd, hh), origin, w, h, d, FaceKind.PosX);
        AddQuad(tex, new Rect(u + ww + dd * 2, v + dd, ww, hh), origin, w, h, d, FaceKind.NegZ);
    }

    private void AddCape(BitmapSource tex, Point3D origin, double w, double h, double d)
    {
        // 披风贴图是 64x32:front(1,1,10,16) back(12,1,10,16)
        AddQuad(tex, new Rect(1, 1, 10, 16), origin, w, h, d, FaceKind.PosZ);
        AddQuad(tex, new Rect(12, 1, 10, 16), origin, w, h, d, FaceKind.NegZ);
    }

    private enum FaceKind { PosX, NegX, PosY, NegY, PosZ, NegZ }

    /// <summary>按"从外部看"的屏幕方向铺一块面:贴图上边=面上边,贴图左边=观察者左边。</summary>
    private void AddQuad(BitmapSource tex, Rect src, Point3D o, double w, double h, double d, FaceKind kind)
    {
        double x0 = o.X, y0 = o.Y, z0 = o.Z, x1 = x0 + w, y1 = y0 + h, z1 = z0 + d;

        Point3D tl, tr, br, bl;
        switch (kind)
        {
            case FaceKind.PosZ:  // 从 +Z 看:左 = -X
                tl = new Point3D(x0, y1, z1); tr = new Point3D(x1, y1, z1);
                br = new Point3D(x1, y0, z1); bl = new Point3D(x0, y0, z1); break;
            case FaceKind.NegZ:  // 从 -Z 看:左 = +X
                tl = new Point3D(x1, y1, z0); tr = new Point3D(x0, y1, z0);
                br = new Point3D(x0, y0, z0); bl = new Point3D(x1, y0, z0); break;
            case FaceKind.NegX:  // 从 -X 看:左 = -Z
                tl = new Point3D(x0, y1, z0); tr = new Point3D(x0, y1, z1);
                br = new Point3D(x0, y0, z1); bl = new Point3D(x0, y0, z0); break;
            case FaceKind.PosX:  // 从 +X 看:左 = +Z
                tl = new Point3D(x1, y1, z1); tr = new Point3D(x1, y1, z0);
                br = new Point3D(x1, y0, z0); bl = new Point3D(x1, y0, z1); break;
            case FaceKind.PosY:  // 从上看:左 = -X,上边 = -Z(背面)
                tl = new Point3D(x0, y1, z0); tr = new Point3D(x1, y1, z0);
                br = new Point3D(x1, y1, z1); bl = new Point3D(x0, y1, z1); break;
            default:             // 从下看:左 = +X,上边 = +Z(正面)
                tl = new Point3D(x1, y0, z1); tr = new Point3D(x0, y0, z1);
                br = new Point3D(x0, y0, z0); bl = new Point3D(x1, y0, z0); break;
        }

        BitmapSource crop;
        try
        {
            var rect = new Int32Rect((int)src.X, (int)src.Y, (int)src.Width, (int)src.Height);
            if (rect.X + rect.Width > tex.PixelWidth || rect.Y + rect.Height > tex.PixelHeight) return;
            crop = new CroppedBitmap(tex, rect);
            crop.Freeze();
        }
        catch (Exception)
        {
            return;
        }

        var mesh = new MeshGeometry3D();
        foreach (var p in new[] { tl, tr, br, bl }) mesh.Positions.Add(p);
        mesh.TextureCoordinates.Add(new Point(0, 0));
        mesh.TextureCoordinates.Add(new Point(1, 0));
        mesh.TextureCoordinates.Add(new Point(1, 1));
        mesh.TextureCoordinates.Add(new Point(0, 1));
        mesh.TriangleIndices.Add(0); mesh.TriangleIndices.Add(1); mesh.TriangleIndices.Add(2);
        mesh.TriangleIndices.Add(0); mesh.TriangleIndices.Add(2); mesh.TriangleIndices.Add(3);

        var brush = new ImageBrush(crop) { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(brush, EdgeMode.Aliased);
        var material = new DiffuseMaterial(brush);
        _root.Children.Add(new ModelVisual3D
        {
            Content = new GeometryModel3D(mesh, material) { BackMaterial = material },
        });
    }
}
