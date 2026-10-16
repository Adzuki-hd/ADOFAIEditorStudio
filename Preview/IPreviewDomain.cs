namespace ADOFAIEditorStudio.Preview
{
    internal interface IPreviewDomain
    {
        string Name { get; }

        void Analyze(PreviewTimelineContext context);

        void Evaluate(PreviewEvaluationFrame frame);

        void EndSession();
    }

    /// <summary>
    /// 由域**可选**实现：播放头发生跳转（拖动 / 瞬间点击）时，引擎会调一次，
    /// 让域把"带历史"的缓存作废。
    ///
    /// 域里那些「携带值 / 已应用缓存 / 会话状态」的设计前提都是**播放头连续
    /// 移动**；跳转打破了它，而在此之前没有任何一处收到通知。表现就是实测的
    /// 「慢拖能还原、快拖和瞬间点击不还原」——慢拖有足够帧数让那些状态自己
    /// 收敛，跳转没有。
    ///
    /// 不直接往 `IPreviewDomain` 里加方法，是为了不必给全部 18 个域都写一个
    /// 空实现；引擎用 `as IHistoryResettable` 逐个问，不实现的自然跳过。
    /// </summary>
    internal interface IHistoryResettable
    {
        void ResetHistory();
    }
}
