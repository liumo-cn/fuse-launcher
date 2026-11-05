// SkinView.cs —— 取自 WPF-Minecraft-Skin-Viewer
// https://github.com/mokkignomov-design/WPF-Minecraft-Skin-Viewer
// MIT License, Copyright (c) 2026 mokkignomov-design
// 改动:命名空间改为 Fuse.App;用本地 Clamp 替代 net48 缺失的 Math.Clamp。
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace Fuse.App;

public class SkinView : Control
{
    private Viewport3D _viewport;
    private Model3DGroup _modelGroup;
    private QuaternionRotation3D _yRotation;
    private QuaternionRotation3D _xRotation;
    private ScaleTransform3D _modelScale;
    private AxisAngleRotation3D? _armLeft, _armRight, _legLeft, _legRight;   // 走路摆动
    private DispatcherTimer? _walkTimer;
    private double _walkPhase;

    private DispatcherTimer _autoRotateTimer;
    private double _currentAngleX = 0;
    private double _currentAngleY = 0;

    public static readonly DependencyProperty SkinSourceProperty = DependencyProperty.Register(
        nameof(SkinSource), typeof(BitmapSource), typeof(SkinView),
        new PropertyMetadata(null, (d, e) => ((SkinView)d).UpdateModel()));

    public BitmapSource SkinSource { get => (BitmapSource)GetValue(SkinSourceProperty); set => SetValue(SkinSourceProperty, value); }

    private bool _walking;
    private bool _faceFront = true;
    private bool _showOverlay;

    /// <summary>
    /// 是否播放行走动画。默认 false —— 构造完就是站立姿态。
    /// 三个姿态开关都会触发重建,所以"先赋值还是后赋值"不影响最终姿态。
    /// </summary>
    public bool Walking
    {
        get => _walking;
        set
        {
            if (_walking == value) return;
            _walking = value;
            UpdateModel();
        }
    }

    /// <summary>固定正面(默认 true:模型初始就朝屏幕,角度恒为 0)。</summary>
    public bool FaceFront
    {
        get => _faceFront;
        set
        {
            if (_faceFront == value) return;
            _faceFront = value;
            if (value) ResetToFront();
        }
    }

    /// <summary>
    /// 是否显示第二层(帽子/外套/袖子/裤腿)。默认 false:单层模型,
    /// 从根上没有两层共面的深度冲突,也就不会闪。
    /// </summary>
    public bool ShowOverlay
    {
        get => _showOverlay;
        set
        {
            if (_showOverlay == value) return;
            _showOverlay = value;
            UpdateModel();
        }
    }

    /// <summary>模型缩放(默认 0.5;独立展示窗设为 1.0)。</summary>
    public double ModelScale
    {
        get => _modelScale.ScaleX;
        set
        {
            _modelScale.ScaleX = value;
            _modelScale.ScaleY = value;
            _modelScale.ScaleZ = value;
        }
    }

    /// <summary>是否自动旋转(不透明窗口里 GPU 渲染有抗锯齿,可以放心转)。</summary>
    public bool AutoRotate
    {
        get => _autoRotateTimer.IsEnabled;
        set
        {
            if (value) _autoRotateTimer.Start();
            else _autoRotateTimer.Stop();
        }
    }

    public SkinView()
    {
        _viewport = new Viewport3D();
        RenderOptions.SetBitmapScalingMode(_viewport, BitmapScalingMode.NearestNeighbor);

        _modelGroup = new Model3DGroup();
        // 相机对准模型几何中心(头 6..14 / 躯干 -6..6 / 腿 -18..-6 → 中心 y=-2),
        // 这样头顶与脚下的留白相等,人物不偏下。
        _viewport.Camera = new PerspectiveCamera(new Point3D(0, -2, 50), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 45)
        {
            // 收紧裁剪面能显著提升深度缓冲精度,减少双层皮肤的边缘闪烁
            NearPlaneDistance = 1,
            FarPlaneDistance = 200,
        };

        var modelVisual = new ModelVisual3D { Content = _modelGroup };
        var transformGroup = new Transform3DGroup();
        _yRotation = new QuaternionRotation3D();
        _xRotation = new QuaternionRotation3D();
        // 先缩放再旋转:默认 0.5 倍(弹窗内嵌预览用);展示小窗把它设成 1.0 填满
        _modelScale = new ScaleTransform3D(1.0, 1.0, 1.0);   // 弹窗内 2 倍展示
        transformGroup.Children.Add(_modelScale);
        transformGroup.Children.Add(new RotateTransform3D(_yRotation));
        transformGroup.Children.Add(new RotateTransform3D(_xRotation));
        modelVisual.Transform = transformGroup;

        _viewport.Children.Add(modelVisual);
        this.AddVisualChild(_viewport);

        _autoRotateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        // 只展示、不交互:始终缓慢自转(用户要求"玩家只用看")
        _autoRotateTimer.Tick += (s, e) => {
            if (FaceFront) return;   // 固定正面时不转
            _currentAngleY += 0.35;
            UpdateRotation();
        };
        // 不启动自转:分层窗口(AllowsTransparency)整窗走软件渲染,3D 无抗锯齿,
        // 旋转时每个面的边缘都会抖动/闪烁。静止展示即可(用户要求只看不交互)。
        // _autoRotateTimer.Start();

    }

    private void UpdateRotation()
    {
        _yRotation.Quaternion = new Quaternion(new Vector3D(0, 1, 0), _currentAngleY);
        _xRotation.Quaternion = new Quaternion(new Vector3D(1, 0, 0), _currentAngleX);
    }

    /// <summary>角度归零 = 正对屏幕(站立正面)。</summary>
    private void ResetToFront()
    {
        _currentAngleY = 0;
        _currentAngleX = 0;
        UpdateRotation();
    }

    private void UpdateModel()
    {
        if (SkinSource == null) return;

        // 每次都从干净状态重建:停掉旧走路计时器、丢掉旧肢体旋转引用。
        // 这样"重新填数据"不会继承上一次的姿态(手臂停在摆动的中间位置)。
        _walkTimer?.Stop();
        _walkTimer = null;
        _armLeft = _armRight = _legLeft = _legRight = null;

        _modelGroup.Children.Clear();
        _modelGroup.Children.Add(new AmbientLight(Colors.White));

        var img = new Image { Source = SkinSource };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        var brush = new VisualBrush(img) { ViewportUnits = BrushMappingMode.Absolute };
        var material = new DiffuseMaterial(brush);

        bool isSlim = false;
        if (SkinSource.PixelWidth >= 64)
        {
            try
            {
                var cb = new CroppedBitmap(SkinSource, new Int32Rect(42, 48, 1, 1));
                byte[] pixel = new byte[4];
                cb.CopyPixels(pixel, 4, 0);
                if (pixel[3] < 128) isSlim = true;
            }
            catch { }
        }

        GeometryModel3D Part(double x, double y, double z, double w, double h, double d,
            int u, int v, double inf, bool isArm = false, bool isLeft = false)
        {
            double actualW = (isArm && isSlim) ? 3 : w;
            double renderX = x + (isArm && isSlim ? (isLeft ? 0.5 : -0.5) : 0);
            var mesh = new MeshGeometry3D();
            AddBoxToMesh(mesh, renderX, y, z, actualW, h, d, u, v, inf);
            return new GeometryModel3D(mesh, material);
        }

        // 头 + 帽子层
        var head = new Model3DGroup();
        head.Children.Add(Part(0, 10, 0, 8, 8, 8, 8, 8, 1.0));
        if (ShowOverlay) head.Children.Add(Part(0, 10, 0, 8, 8, 8, 40, 8, 1.12));
        _modelGroup.Children.Add(head);

        // 躯干 + 外套层
        var body = new Model3DGroup();
        body.Children.Add(Part(0, 0, 0, 8, 12, 4, 20, 20, 1.0));
        if (ShowOverlay) body.Children.Add(Part(0, 0, 0, 8, 12, 4, 20, 36, 1.12));
        _modelGroup.Children.Add(body);

        if (SkinSource.PixelHeight == 64)
        {
            // 四肢各自成组:旋转中心放在肩/髋。z 必须都是 0 ——
            // 之前左臂 -2 / 右臂 +2 的深度错位在透视下就是"一前一后"的行走摆臂姿态。
            AddLimb(-6, 0, 0, 4, 12, 4, 44, 20, 44, 36, 1.14, true, false, out _armLeft);
            AddLimb(6, 0, 0, 4, 12, 4, 36, 52, 52, 52, 1.14, true, true, out _armRight);
            AddLimb(-2, -12, 0, 4, 12, 4, 4, 20, 4, 36, 1.14, false, false, out _legLeft);
            AddLimb(2, -12, 0, 4, 12, 4, 20, 52, 4, 52, 1.14, false, true, out _legRight);

            GeometryModel3D Make(double x, double y, double z, double w, double h, double d,
                int u, int v, double inf, bool isArm, bool isLeft)
                => Part(x, y, z, w, h, d, u, v, inf, isArm, isLeft);

            void AddLimb(double x, double y, double z, double w, double h, double d,
                int uBase, int vBase, int uOverlay, int vOverlay, double inf,
                bool isArm, bool isLeft, out AxisAngleRotation3D rotation)
            {
                var pivot = new Point3D(x + (isArm ? (isLeft ? 1.5 : -1.5) : 0) * 0, isArm ? 6 : -6, 0);
                rotation = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
                var transform = new RotateTransform3D(rotation, pivot);

                var group = new Model3DGroup { Transform = transform };
                group.Children.Add(Make(x, y, z, w, h, d, uBase, vBase, 1.0, isArm, isLeft));
                if (ShowOverlay) group.Children.Add(Make(x, y, z, w, h, d, uOverlay, vOverlay, inf, isArm, isLeft));
                _modelGroup.Children.Add(group);
            }

            if (Walking) StartWalkAnimation();
        }

        // 默认姿态:正面站立。放在最后统一收敛,前面的开关怎么设都落到这里。
        if (FaceFront) ResetToFront();
    }

    /// <summary>走路动画:手臂与腿反相摆动(DispatcherTimer,25fps)。</summary>
    private void StartWalkAnimation()
    {
        _walkTimer?.Stop();
        _walkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _walkTimer.Tick += (_, _) =>
        {
            _walkPhase += 0.18;
            var swing = Math.Sin(_walkPhase) * 28;
            if (_armLeft != null) _armLeft.Angle = swing;
            if (_armRight != null) _armRight.Angle = -swing;
            if (_legLeft != null) _legLeft.Angle = -swing;
            if (_legRight != null) _legRight.Angle = swing;
        };
        _walkTimer.Start();
    }

    private void AddBoxToMesh(MeshGeometry3D mesh, double x, double y, double z, double w, double h, double d, int u, int v, double inf)
    {
        double hw = w / 2 * inf, hh = h / 2 * inf, hd = d / 2 * inf;
        Point3D[] p = {
            new (x-hw, y+hh, z+hd), new (x+hw, y+hh, z+hd), new (x+hw, y-hh, z+hd), new (x-hw, y-hh, z+hd),
            new (x+hw, y+hh, z-hd), new (x-hw, y+hh, z-hd), new (x-hw, y-hh, z-hd), new (x+hw, y-hh, z-hd)
        };

        double tw = SkinSource.PixelWidth, th = SkinSource.PixelHeight;
        double e = 0.001;

        void AddFace(Point3D p1, Point3D p2, Point3D p3, Point3D p4, double uS, double vS, double uW, double vH, bool flipUV = false)
        {
            int b = mesh.Positions.Count;
            mesh.Positions.Add(p1); mesh.Positions.Add(p2); mesh.Positions.Add(p3); mesh.Positions.Add(p4);
            double u1 = (uS + e) / tw, v1 = (vS + e) / th, u2 = (uS + uW - e) / tw, v2 = (vS + vH - e) / th;
            if (flipUV)
            {
                mesh.TextureCoordinates.Add(new Point(u1, v2)); mesh.TextureCoordinates.Add(new Point(u1, v1));
                mesh.TextureCoordinates.Add(new Point(u2, v1)); mesh.TextureCoordinates.Add(new Point(u2, v2));
            }
            else
            {
                mesh.TextureCoordinates.Add(new Point(u1, v1)); mesh.TextureCoordinates.Add(new Point(u2, v1));
                mesh.TextureCoordinates.Add(new Point(u2, v2)); mesh.TextureCoordinates.Add(new Point(u1, v2));
            }
            mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(b + 2); mesh.TriangleIndices.Add(b + 1);
            mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(b + 3); mesh.TriangleIndices.Add(b + 2);
        }

        AddFace(p[0], p[1], p[2], p[3], u, v, w, h);             // Front
        AddFace(p[1], p[4], p[7], p[2], u + w, v, d, h);         // Right
        AddFace(p[4], p[5], p[6], p[7], u + w + d, v, w, h);     // Back
        AddFace(p[5], p[0], p[3], p[6], u - d, v, d, h);         // Left
        AddFace(p[5], p[4], p[1], p[0], u, v - d, w, d);         // Top
        AddFace(p[2], p[7], p[6], p[3], u + w, v - d, w, d, true); // Bottom
    }

    protected override Visual GetVisualChild(int index) => _viewport;
    protected override int VisualChildrenCount => 1;
    protected override Size ArrangeOverride(Size sz) { _viewport.Arrange(new Rect(sz)); return sz; }

    private static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);
}