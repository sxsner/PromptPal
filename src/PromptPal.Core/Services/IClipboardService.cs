namespace PromptPal.Core.Services;

public interface IClipboardService
{
    /// <summary>复制文本到系统剪贴板。</summary>
    /// <returns>true=已写入；false=剪贴板被占用等导致失败，调用方应提示用户而非当作成功。</returns>
    Task<bool> CopyTextAsync(string text);
}
