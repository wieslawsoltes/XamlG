// Pinned upstream source; only runtime assembly resolution is adapted for the test-reference packaging.
// Copyright (c) 2019 Nikita Tsukanov. MIT; see THIRD-PARTY-NOTICES.md.
using System;
using System.Reflection;
using System.Reflection.Emit;
using XamlX.IL;
using XamlX.TypeSystem;

namespace XamlParserTests;

public partial class CompilerTestBase
{
    private static partial IXamlTypeSystem CreateTypeSystem()
    {
        Assembly.Load(typeof(XamlX.Runtime.IXamlParentStackProviderV1).Assembly.GetName());
        return new SreTypeSystem();
    }

    protected partial class TestCompiler
    {
        private AssemblyBuilder _assembly = null!;
        private ModuleBuilder _module = null!;

#if !NETCOREAPP && !NETSTANDARD
        private static readonly object s_assemblyLock = new();
#endif

        private partial void Initialize()
        {
#if !NETCOREAPP && !NETSTANDARD
            _assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(
                new AssemblyName(Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.RunAndSave,
                System.IO.Directory.GetCurrentDirectory());
#else
            _assembly = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName(Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
#endif
            _module = _assembly.DefineDynamicModule("testasm.dll");
        }

        private partial void CompleteCompilation()
        {
#if !NETCOREAPP && !NETSTANDARD
            _module.CreateGlobalFunctions();
            lock (s_assemblyLock) _assembly.Save("testasm.dll");
#endif
        }

        private partial RuntimeTypeBuilder CreateTypeBuilderCore(string name, bool isPublic)
        {
            var attributes = TypeAttributes.Class | (isPublic ? TypeAttributes.Public : TypeAttributes.NotPublic);
            var type = _module.DefineType(name, attributes);
            var typeBuilder = ((SreTypeSystem)_configuration.TypeSystem).CreateTypeBuilder(type);
            return new RuntimeTypeBuilder(typeBuilder, () => type.CreateType()!);
        }
    }
}
