using System;
using System.Collections.Generic;
using UnityEngine;

namespace RuntimeUnityEditor.Core.REPL
{
    internal struct Suggestion
    {
        public readonly string Original;
        public readonly string Result;
        public readonly SuggestionKind Kind;
        public readonly string TypeName;
        public readonly List<MethodOverload> Overloads;

        public Suggestion(string result, string original, SuggestionKind kind, string typeName = null, List<MethodOverload> overloads = null)
        {
            Original = original;
            Kind = kind;
            Result = result;
            TypeName = typeName ?? "";
            Overloads = overloads;
        }

        public Color GetTextColor()
        {
            switch (Kind)
            {
                case SuggestionKind.Namespace:
                    return new Color(0.7f, 0.7f, 0.7f); // Gray
                case SuggestionKind.Class:
                case SuggestionKind.Struct:
                case SuggestionKind.Interface:
                    return new Color(0.31f, 0.79f, 0.69f); // Teal (4EC9B0)
                case SuggestionKind.Enum:
                    return new Color(0.71f, 0.81f, 0.66f); // Light green (B5CEA8)
                case SuggestionKind.Method:
                    return new Color(0.86f, 0.86f, 0.67f); // Yellow (DCDCAA)
                case SuggestionKind.Property:
                case SuggestionKind.Field:
                    return new Color(0.61f, 0.79f, 0.98f); // Light blue
                case SuggestionKind.Event:
                case SuggestionKind.Delegate:
                    return new Color(0.86f, 0.61f, 0.52f); // Salmon (D69D85)
                case SuggestionKind.Keyword:
                    return new Color(0.34f, 0.61f, 0.84f); // Blue (569CD6)
                case SuggestionKind.Variable:
                    return new Color(0.61f, 0.79f, 0.98f); // Light blue
                default:
                    return Color.white;
            }
        }

        public string GetKindIcon()
        {
            switch (Kind)
            {
                case SuggestionKind.Namespace: return "N";
                case SuggestionKind.Class: return "C";
                case SuggestionKind.Struct: return "S";
                case SuggestionKind.Interface: return "I";
                case SuggestionKind.Enum: return "E";
                case SuggestionKind.Method: return "M";
                case SuggestionKind.Property: return "P";
                case SuggestionKind.Field: return "F";
                case SuggestionKind.Event: return "Ev";
                case SuggestionKind.Delegate: return "D";
                case SuggestionKind.Variable: return "V";
                case SuggestionKind.Keyword: return "K";
                default: return "?";
            }
        }

        public string GetDisplayText()
        {
            if (Kind == SuggestionKind.Method && Overloads != null && Overloads.Count > 0)
            {
                return $"{Result}() [{Overloads.Count} overload(s)]";
            }
            if (!string.IsNullOrEmpty(TypeName))
            {
                return $"{Result} : {TypeName}";
            }
            return Result;
        }

        public string GetCompletionText()
        {
            if (Kind == SuggestionKind.Method)
            {
                return Result + "()";
            }
            return Result;
        }
    }

    internal class MethodOverload
    {
        public string ReturnType { get; set; }
        public List<ParameterInfo> Parameters { get; set; }
        public bool IsStatic { get; set; }
        public bool IsGeneric { get; set; }
        public string[] GenericArguments { get; set; }

        public MethodOverload()
        {
            Parameters = new List<ParameterInfo>();
        }

        public string GetSignature(string methodName)
        {
            var paramStr = string.Join(", ", Parameters.ConvertAll(p => p.ToString()).ToArray());
            var genericStr = IsGeneric && GenericArguments != null && GenericArguments.Length > 0
                ? $"<{string.Join(", ", GenericArguments)}>"
                : "";
            var staticStr = IsStatic ? "static " : "";
            return $"{staticStr}{ReturnType} {methodName}{genericStr}({paramStr})";
        }
    }

    internal class ParameterInfo
    {
        public string Name { get; set; }
        public string TypeName { get; set; }
        public bool HasDefaultValue { get; set; }
        public string DefaultValue { get; set; }
        public bool IsParams { get; set; }
        public bool IsOut { get; set; }
        public bool IsRef { get; set; }

        public override string ToString()
        {
            var prefix = IsParams ? "params " : (IsOut ? "out " : (IsRef ? "ref " : ""));
            var suffix = HasDefaultValue ? $" = {DefaultValue}" : "";
            return $"{prefix}{TypeName} {Name}{suffix}";
        }
    }
}