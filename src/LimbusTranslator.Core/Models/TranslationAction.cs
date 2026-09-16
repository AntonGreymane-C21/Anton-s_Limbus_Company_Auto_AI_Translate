namespace LimbusTranslator.Core.Models;

/// <summary>
/// 实际处理动作。与 DiffKind 分离。
/// 例如：英文没变（Unchanged）但旧中文缺失 → TranslationAction 为 TranslateMissing。
/// </summary>
public enum TranslationAction
{
    /// <summary>英文没变且已有旧中文 → 直接继承旧中文</summary>
    Inherit,

    /// <summary>新增文本 → 调用 API 翻译</summary>
    TranslateNew,

    /// <summary>修改文本 → 三路翻译（旧英+旧中+新英）</summary>
    TranslateModified,

    /// <summary>英文没变但旧中文缺失 → 调用 API 翻译</summary>
    TranslateMissing,

    /// <summary>翻译记忆命中 → 直接复用</summary>
    UseTranslationMemory,

    /// <summary>新版本已删除 → 不输出</summary>
    SkipDeleted,
}
