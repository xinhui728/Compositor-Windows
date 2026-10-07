using System.Globalization;
using System.Text;

namespace Compositor.Desktop.Localization;

/// <summary>
/// Safe, centralized display fallbacks for keys introduced by programmatic Desktop controls. Reviewed text lives
/// in the resx files; after the required English-resource fallback, this catalog keeps a newly added key readable
/// until it receives a reviewed entry, rather than ever exposing an implementation key to a person using the editor.
/// </summary>
internal static class ResourceFallbacks
{
    private static readonly HashSet<string> DisplayRoots = new(StringComparer.Ordinal)
    {
        "Adjustment", "Brush", "CameraRaw", "ColorRange", "Common", "Confirm", "CropRatio", "Dialog",
        "Dither", "Effect", "Filter", "Gradient", "GuideAxis", "History", "Layer", "LevelsAuto", "Menu",
        "SelectionAmount", "Shortcut", "Status", "Term", "Toolbar", "Tool", "ToolOptions", "Update",
    };

    // Dynamic choices and history operation names use stable Core values, so they are kept here at the Desktop
    // boundary. This is intentionally display-only: the underlying names are never changed or serialized.
    private static readonly IReadOnlyDictionary<string, string> TraditionalChinese =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CropRatio.Free"] = "自由",
            ["CropRatio.Original"] = "原始比例",
            ["CropRatio.OneToOne"] = "1:1",
            ["CropRatio.FourToThree"] = "4:3",
            ["CropRatio.ThreeToFour"] = "3:4",
            ["CropRatio.SixteenToNine"] = "16:9",
            ["CropRatio.NineToSixteen"] = "9:16",
            ["LevelsAuto.Contrast"] = "對比",
            ["LevelsAuto.Color"] = "色彩",
            ["LevelsAuto.Neutral"] = "色彩與中性中間調",
            ["SelectionAmount.Expand"] = "擴張",
            ["SelectionAmount.Contract"] = "收縮",
            ["SelectionAmount.Feather"] = "羽化",
            ["History.Move Pixels"] = "移動像素",
            ["History.Move Selection"] = "移動選取範圍",
            ["History.Move Layer"] = "移動圖層",
            ["History.Opacity"] = "不透明度",
            ["History.Blend Mode"] = "混合模式",
            ["History.Auto Levels"] = "自動色階",
            ["History.Camera Raw Filter"] = "Camera Raw 濾鏡",
            ["History.Canvas Size"] = "畫布尺寸",
            ["History.Image Size"] = "影像尺寸",
            ["History.Clear"] = "清除",
            ["History.Clear Mask"] = "清除遮色片",
            ["History.Clear Effects"] = "清除效果",
            ["History.Clear Guides"] = "清除參考線",
            ["History.Content-Aware Fill"] = "內容感知填滿",
            ["History.Crop"] = "裁切",
            ["History.Duplicate Layer"] = "複製圖層",
            ["History.Delete Layer Mask"] = "刪除圖層遮色片",
            ["History.Delete Layer"] = "刪除圖層",
            ["History.Delete Layers"] = "刪除圖層",
            ["History.Deselect"] = "取消選取",
            ["History.Dither"] = "網點",
            ["History.Rename Layer"] = "重新命名圖層",
            ["History.New Blank Layer"] = "新增空白圖層",
            ["History.New Folder"] = "新增資料夾",
            ["History.Group Layers"] = "群組圖層",
            ["History.New Adjustment Layer"] = "新增調整圖層",
            ["History.Cut"] = "剪下",
            ["History.Paste"] = "貼上",
            ["History.Layer via Copy"] = "透過拷貝建立圖層",
            ["History.Distort"] = "扭曲",
            ["History.Distort Layers"] = "扭曲圖層",
            ["History.Move Guide"] = "移動參考線",
            ["History.New Guide"] = "新增參考線",
            ["History.Color Range"] = "顏色範圍",
            ["History.Edit Text"] = "編輯文字",
            ["History.Type"] = "文字",
            ["History.Transform"] = "變形",
            ["History.Transform Layers"] = "變形圖層",
            ["History.Import image"] = "匯入影像",
            ["History.Inverse"] = "反轉選取範圍",
            ["History.Magic Wand"] = "魔術棒",
            ["History.Select All"] = "全部選取",
            ["History.Select Layer's Pixels"] = "選取圖層像素",
            ["History.Select Mask's Black Areas"] = "選取遮色片黑色區域",
            ["History.Trim"] = "修剪",
        };

    private static readonly IReadOnlyDictionary<string, string> Terms =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Add"] = "新增", ["Adjustment"] = "調整", ["All"] = "全部", ["Amount"] = "數量",
            ["Angle"] = "角度", ["Apply"] = "套用", ["Background"] = "背景", ["Balance"] = "平衡",
            ["Black"] = "黑色", ["Blue"] = "藍色", ["Blur"] = "模糊", ["Brush"] = "筆刷",
            ["Canvas"] = "畫布", ["Cell"] = "儲存格", ["Channel"] = "色版", ["Characters"] = "字元",
            ["Clear"] = "清除", ["Clipped"] = "裁切", ["Clipping"] = "裁切", ["Clone"] = "仿製",
            ["Color"] = "色彩", ["Colors"] = "色彩",
            ["Contrast"] = "對比", ["Corner"] = "圓角", ["Crop"] = "裁切", ["Curves"] = "曲線",
            ["Cyan"] = "青色", ["Cyanes"] = "青色", ["Cyans"] = "青色", ["Dark"] = "深色",
            ["Delete"] = "刪除", ["Density"] = "密度", ["Difference"] = "差異", ["Disable"] = "停用",
            ["Distance"] = "距離",
            ["Distortion"] = "扭曲", ["Dither"] = "網點", ["Dot"] = "點", ["Draw"] = "繪製",
            ["Effect"] = "效果", ["Effects"] = "效果", ["Elliptical"] = "橢圓", ["Enable"] = "啟用",
            ["Exposure"] = "曝光",
            ["Feather"] = "羽化", ["File"] = "檔案", ["Fill"] = "填滿", ["Filter"] = "濾鏡",
            ["Flip"] = "翻轉", ["Foreground"] = "前景",
            ["Gaussian"] = "高斯", ["Gradient"] = "漸層", ["Green"] = "綠色", ["Grid"] = "格線",
            ["Guide"] = "參考線", ["Guides"] = "參考線", ["Hand"] = "手形", ["Hard"] = "硬",
            ["Hardness"] = "硬度", ["Height"] = "高度", ["Hide"] = "隱藏", ["Highlights"] = "亮部",
            ["History"] = "歷程記錄", ["Horizontal"] = "水平",
            ["Hue"] = "色相", ["Image"] = "影像", ["Inside"] = "內側", ["Invert"] = "反相",
            ["Keep"] = "保留", ["Layer"] = "圖層", ["Layers"] = "圖層", ["Lasso"] = "套索", ["Light"] = "淺色",
            ["Lightness"] = "明度", ["Line"] = "線條", ["Link"] = "連結", ["Mask"] = "遮色片",
            ["Merge"] = "合併", ["Midpoint"] = "中點", ["Midtones"] = "中間調", ["Mode"] = "模式",
            ["On"] = "在",
            ["Monochromatic"] = "單色", ["Move"] = "移動", ["New"] = "新增", ["Noise"] = "雜訊",
            ["Nothing"] = "無", ["Offset"] = "位移", ["Opacity"] = "不透明度", ["Open"] = "開啟",
            ["Output"] = "輸出", ["Percent"] = "百分比", ["Pixel"] = "像素", ["Pixels"] = "像素",
            ["Polygonal"] = "多邊形",
            ["Point"] = "點", ["Preserve"] = "保留", ["Preview"] = "預覽", ["Radius"] = "半徑",
            ["Range"] = "範圍", ["Raw"] = "原始", ["Red"] = "紅色", ["Rectangular"] = "矩形",
            ["Release"] = "釋放", ["Remove"] = "移除", ["Reveal"] = "顯示", ["Reverse"] = "反轉",
            ["Roughness"] = "粗糙度", ["Roundness"] = "圓度", ["Saturation"] = "飽和度", ["Save"] = "儲存",
            ["Selection"] = "選取範圍", ["Shadow"] = "陰影", ["Shadows"] = "陰影", ["Shape"] = "形狀",
            ["Show"] = "顯示", ["Size"] = "大小", ["Snap"] = "貼齊", ["Square"] = "方形",
            ["Stamp"] = "印章",
            ["Style"] = "樣式", ["Text"] = "文字", ["Tint"] = "色調", ["Title"] = "標題",
            ["Tool"] = "工具", ["Toolbar"] = "工具列", ["To"] = "至", ["Tone"] = "色調",
            ["Tones"] = "色調", ["Transform"] = "變形", ["Two"] = "雙", ["Unlink"] = "取消連結",
            ["Update"] = "更新", ["Value"] = "值", ["Vertical"] = "垂直", ["Vignette"] = "暈影",
            ["Wand"] = "魔術棒", ["White"] = "白色",
            ["Width"] = "寬度", ["With"] = "與", ["Yellows"] = "黃色", ["Yellow"] = "黃色",
            ["Zoom"] = "縮放",
        };

    public static string? Get(string key, CultureInfo culture)
    {
        var separator = key.IndexOf('.');
        var root = separator < 0 ? key : key[..separator];
        if (!DisplayRoots.Contains(root)) return null;

        if (!string.Equals(culture.Name, LocalizationManager.TraditionalChineseCulture,
                StringComparison.OrdinalIgnoreCase))
        {
            return Humanize(WithoutRoot(key));
        }

        return TraditionalChinese.TryGetValue(key, out var exact)
            ? exact
            : Translate(WithoutRoot(key));
    }

    private static string WithoutRoot(string key)
    {
        var separator = key.IndexOf('.');
        return separator < 0 ? key : key[(separator + 1)..];
    }

    private static string Humanize(string text)
    {
        var words = SplitWords(text);
        return string.Join(" ", words);
    }

    private static string Translate(string text)
    {
        var words = SplitWords(text);
        var result = new StringBuilder();
        foreach (var word in words)
        {
            result.Append(Terms.TryGetValue(word, out var translation) ? translation : word);
        }
        return result.ToString();
    }

    private static IReadOnlyList<string> SplitWords(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var character in text)
        {
            if (character is '.' or ':' or '_' or '-' or '/' || char.IsWhiteSpace(character))
            {
                AddWord(words, current);
                if (character is ':' or '/') words.Add(character.ToString());
                continue;
            }

            if (current.Length > 0 && char.IsUpper(character) && char.IsLower(current[^1]))
            {
                AddWord(words, current);
            }
            else if (current.Length > 0 && char.IsDigit(character) != char.IsDigit(current[^1]))
            {
                AddWord(words, current);
            }
            current.Append(character);
        }
        AddWord(words, current);
        return words;
    }

    private static void AddWord(List<string> words, StringBuilder current)
    {
        if (current.Length == 0) return;
        words.Add(current.ToString());
        current.Clear();
    }
}
