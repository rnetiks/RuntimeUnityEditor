namespace RuntimeUnityEditor.Core.REPL
{
    internal enum SuggestionKind
    {
        Unknown,
        Namespace,
        Class,
        Struct,
        Interface,
        Enum,
        Method,
        Property,
        Field,
        Event,
        Delegate,
        Variable,
        Keyword
    }
}