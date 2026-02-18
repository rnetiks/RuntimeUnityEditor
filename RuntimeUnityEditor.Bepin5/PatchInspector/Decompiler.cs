using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

public class Disassembler
{
    /// <summary>
    /// Decompiles a Type to C# code
    /// </summary>
    public static string Decompile(Type type)
    {
        if (type == null)
            throw new ArgumentNullException(nameof(type));

        string assemblyPath = type.Assembly.Location;

        if (string.IsNullOrEmpty(assemblyPath))
            throw new InvalidOperationException("Cannot decompile types from dynamic assemblies");

        var resolver = new UniversalAssemblyResolver(assemblyPath, throwOnError: false, null);

        var assemblyDir = System.IO.Path.GetDirectoryName(assemblyPath);
        if (!string.IsNullOrEmpty(assemblyDir))
            resolver.AddSearchDirectory(assemblyDir);

        var decompiler = new CSharpDecompiler(assemblyPath, resolver, new DecompilerSettings());

        var fullTypeName = new FullTypeName(type.FullName);

        return decompiler.DecompileTypeAsString(fullTypeName);
    }

    /// <summary>
    /// Decompiles a MethodInfo to C# code
    /// </summary>
    public static string Decompile(MethodInfo method)
    {
        if (method == null)
            throw new ArgumentNullException(nameof(method));

        string assemblyPath = method.DeclaringType.Assembly.Location;

        if (string.IsNullOrEmpty(assemblyPath))
            throw new InvalidOperationException("Cannot decompile methods from dynamic assemblies");

        var resolver = new UniversalAssemblyResolver(assemblyPath, throwOnError: false, null);

        var assemblyDir = System.IO.Path.GetDirectoryName(assemblyPath);
        if (!string.IsNullOrEmpty(assemblyDir))
            resolver.AddSearchDirectory(assemblyDir);

        var decompiler = new CSharpDecompiler(assemblyPath, resolver, new DecompilerSettings());

        var entityHandle = MetadataTokens.EntityHandle(method.MetadataToken);

        return decompiler.DecompileAsString(entityHandle);
    }

    /// <summary>
    /// Decompiles a Type with custom settings
    /// </summary>
    public static string Decompile(Type type, DecompilerSettings settings)
    {
        if (type == null)
            throw new ArgumentNullException(nameof(type));

        string assemblyPath = type.Assembly.Location;

        if (string.IsNullOrEmpty(assemblyPath))
            throw new InvalidOperationException("Cannot decompile types from dynamic assemblies");

        var resolver = new UniversalAssemblyResolver(assemblyPath, throwOnError: false, null);

        var assemblyDir = System.IO.Path.GetDirectoryName(assemblyPath);
        if (!string.IsNullOrEmpty(assemblyDir))
            resolver.AddSearchDirectory(assemblyDir);

        var decompiler = new CSharpDecompiler(assemblyPath, resolver, settings);
        var fullTypeName = new FullTypeName(type.FullName);

        return decompiler.DecompileTypeAsString(fullTypeName);
    }

    /// <summary>
    /// Decompiles a MethodInfo with custom settings
    /// </summary>
    public static string Decompile(MethodInfo method, DecompilerSettings settings)
    {
        if (method == null)
            throw new ArgumentNullException(nameof(method));

        string assemblyPath = method.DeclaringType.Assembly.Location;

        if (string.IsNullOrEmpty(assemblyPath))
            throw new InvalidOperationException("Cannot decompile methods from dynamic assemblies");

        var resolver = new UniversalAssemblyResolver(assemblyPath, throwOnError: false, null);

        var assemblyDir = System.IO.Path.GetDirectoryName(assemblyPath);
        if (!string.IsNullOrEmpty(assemblyDir))
            resolver.AddSearchDirectory(assemblyDir);

        var decompiler = new CSharpDecompiler(assemblyPath, resolver, settings);

        var entityHandle = MetadataTokens.EntityHandle(method.MetadataToken);

        return decompiler.DecompileAsString(entityHandle);
    }
}