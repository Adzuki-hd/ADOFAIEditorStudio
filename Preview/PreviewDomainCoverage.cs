namespace ADOFAIEditorStudio.Preview
{
    internal enum PreviewDomainState
    {
        Active,
        Planned,
        Unsupported
    }

    internal sealed class PreviewDomainCoverage
    {
        internal PreviewDomainCoverage(string name, PreviewDomainState state, string notes)
        {
            Name = name;
            State = state;
            Notes = notes;
        }

        internal string Name { get; private set; }

        internal PreviewDomainState State { get; private set; }

        internal string Notes { get; private set; }
    }
}
