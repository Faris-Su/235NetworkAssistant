using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RuijieNetworkAssistant.Helpers;

/// <summary>
/// UI 快照工具：在不显示窗口的情况下完成 Measure/Arrange/Render，
/// 用于 Phase 0 的界面验收（不需要人工截图）。
/// </summary>
public static class UiSnapshot
{
    public static void Render(FrameworkElement element, string path, int width, int height)
    {
        // 元素自带 Margin 时（例如对话框根 Grid 的 12px 内边距），
        // 直接按 width×height 布局会把右下角挤到画布外——快照会缺掉最后一条按钮。
        // 这里把 Margin 算进去：内容区 = 画布 - Margin，并按 Margin 偏移摆放。
        var margin = element.Margin;
        var contentWidth = Math.Max(1, width - margin.Left - margin.Right);
        var contentHeight = Math.Max(1, height - margin.Top - margin.Bottom);

        element.Width = contentWidth;
        element.Height = contentHeight;
        element.Measure(new Size(contentWidth, contentHeight));
        element.Arrange(new Rect(margin.Left, margin.Top, contentWidth, contentHeight));
        element.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public static void WriteLog(string snapshotPath, IEnumerable<string> lines)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(snapshotPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllLines(snapshotPath + ".log", lines);
    }

    /// <summary>
    /// 布局溢出检测：找出被父容器裁掉、且所在区域没有任何可见滚动条可以滚到的元素。
    /// 用于自动化验证“小窗口下信息不再丢失”。调用前需先 Measure/Arrange 完成布局。
    /// </summary>
    public static IReadOnlyList<string> FindClippedContent(FrameworkElement root)
    {
        var results = new List<string>();
        var rootBounds = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
        if (rootBounds.Width <= 0 || rootBounds.Height <= 0)
        {
            return results;
        }

        Walk(root, root, rootBounds, results, false);
        return results;
    }

    /// <summary>
    /// 列出界面里实际可滚动的区域（滚动条可见或内容超出可视范围），用于验证滚动条是否真的出现。
    /// </summary>
    public static IReadOnlyList<string> DescribeScrollRegions(FrameworkElement root)
    {
        var results = new List<string>();
        Collect(root, results);
        return results;
    }

    private static void Collect(DependencyObject node, List<string> results)
    {
        if (node is DataGrid grid)
        {
            var visible = grid.Columns.Where(c => c.Visibility == Visibility.Visible).ToList();
            var total = visible.Sum(c => double.IsNaN(c.ActualWidth) ? 0 : c.ActualWidth);
            results.Add(
                $"DataGrid[{grid.Name}] 宽={grid.ActualWidth:0}，可见列 {visible.Count}/{grid.Columns.Count}，" +
                $"列宽合计={total:0}");
        }

        if (node is ScrollViewer scroller &&
            (scroller.ScrollableWidth > 0.5 || scroller.ScrollableHeight > 0.5))
        {
            var name = string.IsNullOrEmpty(scroller.Name) ? "(匿名)" : scroller.Name;
            results.Add(
                $"{name} 可滚 {scroller.ScrollableWidth:0}x{scroller.ScrollableHeight:0}，" +
                $"竖条={scroller.ComputedVerticalScrollBarVisibility}，横条={scroller.ComputedHorizontalScrollBarVisibility}");
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            Collect(VisualTreeHelper.GetChild(node, i), results);
        }
    }

    private static void Walk(
        DependencyObject node,
        FrameworkElement root,
        Rect rootBounds,
        List<string> results,
        bool insideScrollable)
    {
        // 折叠（Collapsed）的子树不参与布局：它的子元素会保留上一次测量的尺寸，
        // 直接跳过，否则会把“本来就不显示的东西”误报成被裁切。
        if (node is UIElement { Visibility: not Visibility.Visible })
        {
            return;
        }

        // 只要祖先里有“滚动条实际可见”的 ScrollViewer，用户就能滚到，不再算作被裁切。
        if (node is ScrollViewer scroller &&
            (scroller.ComputedVerticalScrollBarVisibility == Visibility.Visible ||
             scroller.ComputedHorizontalScrollBarVisibility == Visibility.Visible))
        {
            insideScrollable = true;
        }

        if (!insideScrollable && node is FrameworkElement element && element != root)
        {
            var width = element.ActualWidth;
            var height = element.ActualHeight;
            if (width > 1 && height > 1)
            {
                Point origin;
                try
                {
                    origin = element.TransformToAncestor(root).Transform(new Point(0, 0));
                }
                catch (InvalidOperationException)
                {
                    origin = new Point(double.NaN, double.NaN);
                }

                if (!double.IsNaN(origin.X) && !double.IsNaN(origin.Y))
                {
                    var bounds = new Rect(origin, new Size(width, height));
                    if (!rootBounds.Contains(bounds))
                    {
                        results.Add(Describe(element, bounds, rootBounds));
                    }
                }
            }
        }

        // DataGrid 自己就是滚动容器：只判断表格整体是否被裁切，
        // 内部（表头填充列、虚拟化面板等）的越界属于表格自己的滚动机制，不算信息丢失。
        if (node is DataGrid)
        {
            return;
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            Walk(VisualTreeHelper.GetChild(node, i), root, rootBounds, results, insideScrollable);
        }
    }

    private static string Describe(FrameworkElement element, Rect bounds, Rect rootBounds)
    {
        var over = bounds.Bottom - rootBounds.Bottom;
        var side = bounds.Right - rootBounds.Right;
        var reason = over > 0.5
            ? $"超出底部 {over:0}px"
            : side > 0.5
                ? $"超出右侧 {side:0}px"
                : "超出可见区域";

        return $"{reason} · {element.GetType().Name} · {Snippet(element)} · " +
               $"位置({bounds.X:0},{bounds.Y:0}) 尺寸({bounds.Width:0}x{bounds.Height:0}) 可见性={element.Visibility}";
    }

    private static string Snippet(FrameworkElement element)
    {
        var text = element switch
        {
            TextBlock textBlock => textBlock.Text,
            TextBox textBox => textBox.Text,
            Expander expander => expander.Header?.ToString(),
            Button button => button.Content?.ToString(),
            CheckBox checkBox => checkBox.Content?.ToString(),
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(text))
        {
            return element.Name is { Length: > 0 } name ? $"#{name}" : "(容器)";
        }

        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 40 ? text : text[..40] + "…";
    }
}
