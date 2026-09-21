using System.Collections.Generic;

namespace ADOFAIEditorStudio.Preview
{
    internal static class PreviewKeyframeSearch
    {
        internal static int FindLastAtOrBefore<T>(IList<T> keyframes, double time)
            where T : IPreviewTimedKeyframe
        {
            int low = 0;
            int high = keyframes.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (keyframes[middle].Start <= time)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return low - 1;
        }
    }
}
