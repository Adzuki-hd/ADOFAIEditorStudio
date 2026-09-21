namespace ADOFAIEditorStudio.Preview
{
    internal sealed class PreviewEvaluationFrame
    {
        internal PreviewEvaluationFrame(PreviewTimelineContext context, float time)
        {
            Context = context;
            Time = time;
        }

        internal PreviewTimelineContext Context { get; private set; }

        internal float Time { get; private set; }
    }
}
