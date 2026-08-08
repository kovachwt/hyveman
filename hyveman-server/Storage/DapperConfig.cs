using System.Reflection;
using Dapper;

namespace Hyveman.Server.Storage;

/// <summary>
/// Maps SQLite's snake_case columns onto PascalCase record members (Dapper's default is
/// case-insensitive but underscore-blind). Registered once for all repository row types.
/// </summary>
public static class DapperConfig
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        foreach (var t in typeof(Db).Assembly.GetTypes())
        {
            if (t.Namespace?.StartsWith("Hyveman.Server.Storage.Repos", StringComparison.Ordinal) == true)
                SqlMapper.SetTypeMap(t, new SnakeCaseTypeMap(t));
        }
    }

    private sealed class SnakeCaseTypeMap : SqlMapper.ITypeMap
    {
        private readonly Type _type;
        private readonly Dictionary<string, SqlMapper.IMemberMap> _members;

        public SnakeCaseTypeMap(Type type)
        {
            _type = type;
            _members = new Dictionary<string, SqlMapper.IMemberMap>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (p.GetSetMethod(true) is not null)
                    _members[p.Name] = new MemberMapImpl(p);
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (!f.IsInitOnly && f.Name != "<>c__DisplayClass")
                    _members[f.Name] = new MemberMapImpl(f);
        }

        public ConstructorInfo? FindConstructor(string[] names, Type[] types)
        {
            foreach (var ctor in _type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var ps = ctor.GetParameters();
                if (ps.Length == 0) return ctor;   // parameterless → property-based materialization
                if (ps.Length != names.Length) continue;
                var ok = true;
                for (var i = 0; i < ps.Length; i++)
                {
                    if (!string.Equals(ps[i].Name, ToPascal(names[i]), StringComparison.OrdinalIgnoreCase))
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok) return ctor;
            }
            return null;
        }

        public ConstructorInfo? FindExplicitConstructor()
        {
            // Prefer the positional record constructor when unambiguous.
            var ctors = _type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return ctors.Length == 1 ? ctors[0] : null;
        }

        public SqlMapper.IMemberMap? GetConstructorParameter(ConstructorInfo ctor, string name)
        {
            var ps = ctor.GetParameters();
            var target = ToPascal(name);
            for (var i = 0; i < ps.Length; i++)
                if (string.Equals(ps[i].Name, target, StringComparison.OrdinalIgnoreCase))
                    return new MemberMapImpl(ctor, i);
            return null;
        }

        public SqlMapper.IMemberMap? GetMember(string columnName)
            => _members.TryGetValue(ToPascal(columnName), out var m) ? m : null;

        private static string ToPascal(string s)
        {
            var parts = s.Split('_');
            return string.Concat(parts.Select(p =>
                p.Length == 0 ? "" : char.ToUpperInvariant(p[0]) + p[1..]));
        }
    }

    /// <summary>Minimal IMemberMap implementation (Dapper's concrete maps are internal in 2.1.66).</summary>
    private sealed class MemberMapImpl : SqlMapper.IMemberMap
    {
        private readonly PropertyInfo? _property;
        private readonly FieldInfo? _field;
        private readonly ParameterInfo? _parameter;

        public MemberMapImpl(PropertyInfo p) { _property = p; ColumnName = p.Name; MemberType = p.PropertyType; }
        public MemberMapImpl(FieldInfo f) { _field = f; ColumnName = f.Name; MemberType = f.FieldType; }
        public MemberMapImpl(ConstructorInfo ctor, int index)
        {
            _parameter = ctor.GetParameters()[index];
            ColumnName = _parameter.Name!;
            MemberType = _parameter.ParameterType;
        }

        public string ColumnName { get; }
        public Type MemberType { get; }
        public PropertyInfo? Property => _property;
        public FieldInfo? Field => _field;
        public ParameterInfo? Parameter => _parameter;
    }
}
