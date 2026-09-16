namespace LimbusTranslator.Core.Models;

/// <summary>
/// TranslationUnit 工厂。
/// 把 DiffEntry 转成 TranslationUnit；TranslationAgent（TM 写入）与
/// 人工审核写回（HumanReviewed）必须使用同一转换，避免两处字段语义漂移。
/// </summary>
public static class TranslationUnitFactory
{
    /// <summary>
    /// 从 DiffEntry 构造 TranslationUnit。
    /// SourceText 取新版英文，缺失时退回旧版英文（与 Agent 原行为一致）。
    /// </summary>
    public static TranslationUnit FromDiffEntry(DiffEntry entry)
    {
        return new TranslationUnit
        {
            Key = entry.Key,
            FilePath = entry.Key.RelativeFilePath,
            RecordId = entry.Key.RecordId,
            FieldPath = entry.Key.FieldPath,
            SourceText = entry.NewSourceText ?? entry.OldSourceText ?? string.Empty,
            Translation = entry.Translation,
            Speaker = entry.Speaker,
            Order = entry.Order,
            // 第9.0B-P2修复：必须带上传入条目的 Mode Salt。
            // 修复前该字段被丢弃 ⇒ Agent 查询 TM 用「盐哈希」、落库用「无盐哈希」，
            // 导致 ① KR 模式 TM 永不命中（每次重跑都重新调用 API）
            //      ② KR 模式写入的记录会被 EN_ONLY 命中（跨模式污染汉化结果）。
            SourceHashSalt = entry.SourceHashSalt,
        };
    }
}
