namespace LimbusTranslator.Infrastructure.Configuration;

/// <summary>
/// Thinking 模式（第8.75轮）。
///
/// 配置优先级（见 AppSettingsLoader.LoadProviderSettings）：
///   1. 显式 <c>thinkingMode</c>（adaptive / always_on / always_off）——最高优先级
///   2. 旧配置 <c>thinking</c>（true → always_on，false → always_off）——向后兼容
///   3. 两者都缺省 → <see cref="Adaptive"/>（生产默认）
/// </summary>
public enum TranslationThinkingMode
{
    /// <summary>自适应：按条目的来源语言异常 / 文本分类决定是否开启思考（生产默认）。</summary>
    Adaptive = 0,

    /// <summary>始终开启思考（等价旧配置 thinking=true）。</summary>
    AlwaysOn = 1,

    /// <summary>始终关闭思考（等价旧配置 thinking=false）。</summary>
    AlwaysOff = 2,
}
