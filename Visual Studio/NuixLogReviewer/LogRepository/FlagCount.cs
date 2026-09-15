namespace NuixLogReviewer.LogRepository
{
    /// <summary>
    /// Display item for the classifier table: a flag name plus its hit count within the current
    /// filtered result set.
    /// </summary>
    public class FlagCount
    {
        public string Name { get; set; }
        public int Count { get; set; }
    }
}
