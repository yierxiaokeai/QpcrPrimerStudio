using System.Reflection;
using Bio.Registration;

namespace QpcrPrimerStudio.Core;

internal static class BioRuntimeCompatibility
{
    internal static void Initialize()
    {
        // NetBio 3.0.0-alpha scans every DLL beside Bio.Core, including the runtime's
        // CoreLib in self-contained apps. Register its built-ins without that scan.
        const string supportedSha256 = "3554F64E4F8E39637DE993732478C6CC890E55870270635EE7712B586FEF7D90";
        var registration = typeof(BioRegistrationService);
        var assembly = registration.Assembly;
        if (Hashing.File(Path.Combine(AppContext.BaseDirectory, "Bio.Core.dll")) != supportedSha256)
            throw new InvalidOperationException("序列解析库版本已变化，需要重新验证便携版兼容配置。");
        var cache = registration.GetField("locatedParts", BindingFlags.Static | BindingFlags.NonPublic);
        if (cache is null || cache.IsInitOnly || cache.FieldType != typeof(List<BioRegisterAttribute>))
            throw new InvalidOperationException("序列解析库的内置组件注册接口与已验证版本不一致。");
        if (cache.GetValue(null) is null)
            cache.SetValue(null, assembly.GetCustomAttributes<BioRegisterAttribute>().ToList());
    }
}
