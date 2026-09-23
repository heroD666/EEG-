using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace WebView2Plots
{
    /// <summary>
    /// 3D 曲面类型
    /// </summary>
    public enum ThreeDSurfaceType
    {
        /// <summary>具有网格线的曲面（3D Mesh）</summary>
        Surface,
        /// <summary>瀑布图（平滑着色曲面）</summary>
        Waterfall
    }

    /// <summary>
    /// 内部工具：HTML 加载、JSON 构建、JS 转义
    /// </summary>
    internal static class PlotHelper
    {
        internal static async Task<bool> EnsureHtmlLoadedAsync(WebView2 webView, string templateHtml,
            Dictionary<WebView2, string> loadedMap)
        {
            string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, templateHtml);
            if (!File.Exists(htmlPath))
            {
                MessageBox.Show(templateHtml + " not found at: " + htmlPath);
                return false;
            }

            if (!loadedMap.TryGetValue(webView, out string? loadedTemplate) || loadedTemplate != templateHtml)
            {
                webView.Source = new Uri(htmlPath);

                var tcs = new TaskCompletionSource<bool>();
                EventHandler<Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs>? handler = null;
                handler = (s, args) =>
                {
                    webView.NavigationCompleted -= handler;
                    tcs.SetResult(true);
                };
                webView.NavigationCompleted += handler;
                await tcs.Task;
                loadedMap[webView] = templateHtml;
            }
            return true;
        }

        /// <summary>网格数据展开为 [[x,y,z],...] JSON</summary>
        internal static string BuildGridJson(double[,] data, double x0, double xStep, double y0, double yStep)
        {
            int xCount = data.GetLength(0);
            int yCount = data.GetLength(1);
            var sb = new StringBuilder();
            sb.Append("[");
            bool first = true;
            for (int i = 0; i < xCount; i++)
            {
                for (int j = 0; j < yCount; j++)
                {
                    if (!first) sb.Append(",");
                    first = false;
                    double xVal = x0 + i * xStep;
                    double yVal = y0 + j * yStep;
                    sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                        "[{0},{1},{2}]", Num(xVal), Num(yVal), Num(data[i, j]));
                }
            }
            sb.Append("]");
            return sb.ToString();
        }

        /// <summary>数值转不变区域字符串（NaN/Infinity 输出 null）</summary>
        internal static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            return v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>转义单引号 JS 字符串</summary>
        internal static string EscapeJs(string? s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("'", "\\'");
        }
    }

    /// <summary>
    /// 3D 曲面图 / 瀑布图渲染（ECharts GL surface 系列）
    /// </summary>
    public static class WebView2ThreeDSurface
    {
        private static readonly Dictionary<WebView2, string> _loadedMap = new();

        /// <summary>
        /// 渲染 3D 曲面图或瀑布图
        /// </summary>
        /// <param name="webView">目标 WebView2 控件</param>
        /// <param name="data">二维数组 data[xIdx, yIdx]，值为 Z 高度</param>
        /// <param name="x0">X 轴起始值</param>
        /// <param name="xStep">X 轴步进</param>
        /// <param name="y0">Y 轴起始值</param>
        /// <param name="yStep">Y 轴步进</param>
        /// <param name="xAxisName">X 轴名称</param>
        /// <param name="yAxisName">Y 轴名称</param>
        /// <param name="zAxisName">Z 轴名称</param>
        /// <param name="surfaceType">Surface=带网格曲面；Waterfall=瀑布图</param>
        /// <param name="title">图表标题（可选）</param>
        public static async Task RenderAsync(WebView2 webView, double[,] data,
            double x0, double xStep, double y0, double yStep,
            string xAxisName = "X", string yAxisName = "Y", string zAxisName = "Z",
            ThreeDSurfaceType surfaceType = ThreeDSurfaceType.Surface,
            string? title = null)
        {
            int xCount = data.GetLength(0);
            int yCount = data.GetLength(1);
            string jsonData = PlotHelper.BuildGridJson(data, x0, xStep, y0, yStep);

            string templateHtml = surfaceType == ThreeDSurfaceType.Waterfall
                ? "threeDWaterfallWeb.html" : "threeDSurfaceWeb.html";

            if (!await PlotHelper.EnsureHtmlLoadedAsync(webView, templateHtml, _loadedMap))
                return;

            string script = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "renderSurface({0}, {1}, {2}, '{3}', '{4}', '{5}', '{6}');",
                jsonData, xCount, yCount,
                PlotHelper.EscapeJs(xAxisName), PlotHelper.EscapeJs(yAxisName), PlotHelper.EscapeJs(zAxisName),
                PlotHelper.EscapeJs(title));
            await webView.ExecuteScriptAsync(script);
        }
    }
}
