using Microsoft.CodeAnalysis;

namespace Obsessive.Defender.HeapAnalysis
{
    internal static class SyntaxHelper
    {
        public static T FindContainer<T>(this SyntaxNode tokenParent) where T : SyntaxNode
        {
            if (tokenParent is T invocation)
            {
                return invocation;
            }

            return tokenParent.Parent == null ? null : FindContainer<T>(tokenParent.Parent);
        }
    }
}