using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Assistant.Host.Mcp;
using Assistant.Sdk;

namespace Assistant.Host.Services;

public class HostMcpToolExecutor : IMcpToolExecutor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMcpToolAccess _toolAccess;
    private readonly PluginRegistry _registry;
    private readonly List<IDataSourceProvider> _providers;

    private Dictionary<string, (MethodInfo Method, Type ToolType)>? _toolMap;

    public HostMcpToolExecutor(
        IServiceScopeFactory scopeFactory,
        IMcpToolAccess toolAccess,
        PluginRegistry registry,
        IEnumerable<IDataSourceProvider> providers)
    {
        _scopeFactory = scopeFactory;
        _toolAccess = toolAccess;
        _registry = registry;
        _providers = providers.ToList();
    }

    private Dictionary<string, (MethodInfo Method, Type ToolType)> GetToolMap()
    {
        if (_toolMap != null) return _toolMap;

        _toolMap = new Dictionary<string, (MethodInfo, Type)>();

        // Host-level tools always available
        AddToolsFromType(typeof(InformationStoreMcpTools));

        // Plugin tools
        foreach (var plugin in _registry.AllPlugins)
            foreach (var toolType in plugin.GetMcpToolTypes())
                AddToolsFromType(toolType);

        // Data source provider tools
        foreach (var provider in _providers)
            foreach (var toolType in provider.GetMcpToolTypes())
                AddToolsFromType(toolType);

        return _toolMap;
    }

    private void AddToolsFromType(Type toolType)
    {
        foreach (var method in toolType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
        {
            var attr = method.GetCustomAttribute<McpServerToolAttribute>();
            if (attr == null) continue;
            var name = attr.Name ?? method.Name;
            _toolMap![name] = (method, toolType);
        }
    }

    public List<McpToolDef> GetToolDefinitions(string callingPluginId)
    {
        var allowed = _toolAccess.GetAllowedTools(callingPluginId);
        if (allowed.Count == 0) return [];

        var toolMap = GetToolMap();
        var defs = new List<McpToolDef>();

        foreach (var toolName in allowed)
        {
            if (!toolMap.TryGetValue(toolName, out var entry)) continue;
            defs.Add(BuildDef(toolName, entry.Method));
        }

        return defs;
    }

    public List<McpToolDef> GetAllToolDefinitions()
    {
        var toolMap = GetToolMap();
        return toolMap.Select(kv => BuildDef(kv.Key, kv.Value.Method)).ToList();
    }

    private static McpToolDef BuildDef(string name, MethodInfo method)
    {
        var desc = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
        var parameters = new Dictionary<string, McpToolParam>();
        foreach (var param in method.GetParameters())
        {
            if (IsServiceParameter(param)) continue;
            var paramDesc = param.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
            var paramType = MapType(param.ParameterType);
            parameters[param.Name!] = new McpToolParam(paramType, paramDesc, !param.HasDefaultValue);
        }
        return new McpToolDef(name, desc, parameters);
    }

    public async Task<string> ExecuteToolAsync(string toolName, Dictionary<string, object?> arguments)
    {
        var toolMap = GetToolMap();
        if (!toolMap.TryGetValue(toolName, out var entry))
            return JsonSerializer.Serialize(new { error = $"Tool '{toolName}' not found" });

        var (method, toolType) = entry;

        using var scope = _scopeFactory.CreateScope();

        var methodParams = method.GetParameters();
        var args = new object?[methodParams.Length];

        for (int i = 0; i < methodParams.Length; i++)
        {
            var param = methodParams[i];
            if (IsServiceParameter(param))
            {
                args[i] = scope.ServiceProvider.GetRequiredService(param.ParameterType);
            }
            else if (arguments.TryGetValue(param.Name!, out var value))
            {
                args[i] = ConvertArgument(value, param.ParameterType);
            }
            else if (param.HasDefaultValue)
            {
                args[i] = param.DefaultValue;
            }
        }

        // Use ActivatorUtilities so tool types don't need to be individually DI-registered
        object? instance = method.IsStatic
            ? null
            : ActivatorUtilities.CreateInstance(scope.ServiceProvider, toolType);

        var result = method.Invoke(instance, args);

        if (result is Task<string> taskStr) return await taskStr;
        if (result is Task task) { await task; return "{}"; }
        return result?.ToString() ?? "{}";
    }

    private static bool IsServiceParameter(ParameterInfo param)
    {
        if (param.GetCustomAttribute<DescriptionAttribute>() != null) return false;
        if (param.ParameterType == typeof(string) || param.ParameterType.IsPrimitive) return false;
        if (param.ParameterType.IsEnum) return false;
        if (Nullable.GetUnderlyingType(param.ParameterType) != null) return false;
        return true;
    }

    private static object? ConvertArgument(object? value, Type targetType)
    {
        if (value == null) return null;
        if (value is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.String  => ConvertString(je.GetString(), targetType),
                JsonValueKind.Number when targetType == typeof(int)    => je.GetInt32(),
                JsonValueKind.Number when targetType == typeof(long)   => je.GetInt64(),
                JsonValueKind.Number when targetType == typeof(double) => je.GetDouble(),
                JsonValueKind.Number  => je.GetInt32(),
                JsonValueKind.True   => true,
                JsonValueKind.False  => false,
                JsonValueKind.Null   => null,
                _                    => je.GetRawText(),
            };
        }
        return ConvertString(value.ToString(), targetType);
    }

    private static object? ConvertString(string? value, Type targetType)
    {
        if (value == null) return null;
        if (targetType == typeof(string)) return value;
        if (targetType == typeof(int))    return int.Parse(value);
        if (targetType == typeof(long))   return long.Parse(value);
        if (targetType == typeof(double)) return double.Parse(value);
        if (targetType == typeof(bool))   return bool.Parse(value);
        return value;
    }

    private static string MapType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(string))            return "string";
        if (underlying == typeof(int) || underlying == typeof(long))    return "integer";
        if (underlying == typeof(double) || underlying == typeof(float)) return "number";
        if (underlying == typeof(bool))              return "boolean";
        return "string";
    }
}
