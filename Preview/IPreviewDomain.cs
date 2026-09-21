namespace ADOFAIEditorStudio.Preview
{
    internal interface IPreviewDomain
    {
        string Name { get; }

        void Analyze(PreviewTimelineContext context);

        void Evaluate(PreviewEvaluationFrame frame);

        void EndSession();
    }
}
