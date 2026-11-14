using Mono.Cecil;
using Mono.Cecil.Cil;

foreach (var kind in new[] { "valid", "missing-type", "missing-member" })
{
    using var module = ModuleDefinition.CreateModule(kind, ModuleKind.Dll);
    var core = new AssemblyNameReference("Lidarr.Core", new Version(2, 0, 0, 0));
    module.AssemblyReferences.Add(core);
    var target = new TypeReference("NzbDrone.Core.HealthCheck", kind == "missing-type" ? "AbsentTypeControl" : "HealthCheck", module, core);
    var container = new TypeDefinition("Control", "Fixture", TypeAttributes.Public, module.TypeSystem.Object);
    module.Types.Add(container);
    container.Fields.Add(new FieldDefinition("Target", FieldAttributes.Public, target));
    var method = new MethodDefinition("Probe", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
    container.Methods.Add(method);
    // Known real public constructor versus a deliberately nonexistent direct member.
    var reference = new MethodReference(kind == "missing-member" ? "AbsentMemberControl" : ".ctor", module.TypeSystem.Void, target) { HasThis = true };
    reference.Parameters.Add(new ParameterDefinition(module.ImportReference(typeof(Type))));
    var il = method.Body.GetILProcessor();
    il.Emit(OpCodes.Ldnull);
    il.Emit(OpCodes.Newobj, reference);
    il.Emit(OpCodes.Pop);
    il.Emit(OpCodes.Ret);
    module.Write(Path.Combine(args[0], kind + ".dll"));
}
