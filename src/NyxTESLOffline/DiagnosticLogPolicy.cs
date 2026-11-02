using System;
namespace NyxTESLOffline
{
    internal static class DiagnosticLogPolicy
    {
        internal static readonly bool Verbose=true;
        internal static bool Keep(string text,bool verbose)
        {
            if(verbose || text==null)return true;
            if(text.StartsWith("[CARD-RULE]",StringComparison.Ordinal))return false;
            if(text.StartsWith("visual protocol",StringComparison.Ordinal))return text.StartsWith("visual protocol PlayCardToBoard:",StringComparison.Ordinal);
            foreach(string prefix in new[]{"allowed loopback HTTP:","local HTTP POST ","local HTTP GET ","history emit:","attack command ","play command ","CardCommandList sent:","trigger CardCommandList sent:","retail FX selected:","avatar FX anchor retained:","recovered retail rule:","trigger candidate", "avatar visual bound:","attack animation preflight:"})
                if(text.StartsWith(prefix,StringComparison.Ordinal))return false;
            return true;
        }
    }
}
