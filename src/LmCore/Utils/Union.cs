using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

public abstract record Union
{
    protected readonly Type _type;

    protected Union(Type type)
    {
        _type = type;
    }

    public bool Is<T>()
    {
        return typeof(T) == _type;
    }

    public virtual T Get<T>()
    {
        throw new InvalidOperationException("Type not found");
    }

    public virtual void Serialize(Utf8JsonWriter writer, JsonSerializerOptions options)
    {
        throw new JsonException("Invalid Union type");
    }
}

public record Union<T1, T2> : Union
{
    private readonly T1? _v1;

    private readonly T2? _v2;

    protected Union(Type type)
        : base(type) { }

    public Union(T1 value)
        : base(typeof(T1))
    {
        _v1 = value;
    }

    public Union(T2 value)
        : base(typeof(T2))
    {
        _v2 = value;
    }

    public Union(Union<T1, T2> other)
        : base(other)
    {
        _v1 = other._v1;
        _v2 = other._v2;
    }

    public override T Get<T>()
    {
        if (typeof(T).IsAssignableFrom(typeof(T1)))
        {
            return (T)(object)_v1!;
        }
        else if (typeof(T).IsAssignableFrom(typeof(T2)))
        {
            return (T)(object)_v2!;
        }

        return base.Get<T>();
    }

    public static implicit operator T1(Union<T1, T2> union) => union.Get<T1>();

    public static implicit operator T2(Union<T1, T2> union) => union.Get<T2>();

    public static implicit operator Union<T1, T2>(T1 value) => new Union<T1, T2>(value);

    public static implicit operator Union<T1, T2>(T2 value) => new Union<T1, T2>(value);

    public override void Serialize(Utf8JsonWriter writer, JsonSerializerOptions options)
    {
        if (_type == typeof(T2))
        {
            JsonSerializer.Serialize(writer, _v2, options);
        }
        else if (_type == typeof(T1))
        {
            JsonSerializer.Serialize(writer, _v1, options);
        }
        else
        {
            base.Serialize(writer, options);
        }
    }

    protected static Union<T1, T2>? DeserializeInternal(string json, JsonSerializerOptions options)
    {
        try
        {
            var value1 = JsonSerializer.Deserialize<T1>(json, options);
            return new Union<T1, T2>(value1!);
        }
        catch (JsonException) { }

        try
        {
            var value2 = JsonSerializer.Deserialize<T2>(json, options);
            return new Union<T1, T2>(value2!);
        }
        catch (JsonException) { }

        return null;
    }

    public static Union<T1, T2> Deserialize(string json, JsonSerializerOptions options)
    {
        return DeserializeInternal(json, options) ?? throw new JsonException("Invalid JSON");
    }
}

public record Union<T1, T2, T3> : Union<T1, T2>
{
    protected readonly T3? _v3;

    protected Union(Type type)
        : base(type) { }

    public Union(T1 value)
        : base(value!) { }

    public Union(T2 value)
        : base(value!) { }

    public Union(T3 value)
        : base(typeof(T3))
    {
        _v3 = value;
    }

    public Union(Union<T1, T2> other)
        : base(other) { }

    public Union(Union<T1, T2, T3> other)
        : base(other as Union<T1, T2>)
    {
        _v3 = other._v3;
    }

    public override T Get<T>()
    {
        return typeof(T).IsAssignableFrom(typeof(T3)) ? (T)(object)_v3! : base.Get<T>();
    }

    public static implicit operator T1(Union<T1, T2, T3> union) => union.Get<T1>();

    public static implicit operator T2(Union<T1, T2, T3> union) => union.Get<T2>();

    public static implicit operator T3(Union<T1, T2, T3> union) => union.Get<T3>();

    public static implicit operator Union<T1, T2, T3>(T1 value) => new Union<T1, T2, T3>(value);

    public static implicit operator Union<T1, T2, T3>(T2 value) => new Union<T1, T2, T3>(value);

    public static implicit operator Union<T1, T2, T3>(T3 value) => new Union<T1, T2, T3>(value);

    public override void Serialize(Utf8JsonWriter writer, JsonSerializerOptions options)
    {
        if (_type == typeof(T3))
        {
            JsonSerializer.Serialize(writer, _v3, options);
        }
        else
        {
            base.Serialize(writer, options);
        }
    }

    protected static new Union<T1, T2, T3>? DeserializeInternal(string json, JsonSerializerOptions options)
    {
        if (Union<T1, T2>.DeserializeInternal(json, options) is Union<T1, T2> union)
        {
            return new Union<T1, T2, T3>(union);
        }

        try
        {
            var value3 = JsonSerializer.Deserialize<T3>(json, options);
            return new Union<T1, T2, T3>(value3!);
        }
        catch (JsonException) { }

        return null;
    }

    public static new Union<T1, T2, T3> Deserialize(string json, JsonSerializerOptions options)
    {
        return DeserializeInternal(json, options) ?? throw new JsonException("Invalid JSON");
    }
}

public record Union<T1, T2, T3, T4> : Union<T1, T2, T3>
{
    protected readonly T4? _v4;

    protected Union(Type type)
        : base(type) { }

    public Union(T1 value)
        : base(value!) { }

    public Union(T2 value)
        : base(value!) { }

    public Union(T3 value)
        : base(value!) { }

    public Union(T4 value)
        : base(typeof(T4))
    {
        _v4 = value;
    }

    public Union(Union<T1, T2> other)
        : base(other) { }

    public Union(Union<T1, T2, T3> other)
        : base(other) { }

    public Union(Union<T1, T2, T3, T4> other)
        : base(other as Union<T1, T2, T3>)
    {
        _v4 = other._v4;
    }

    public override T Get<T>()
    {
        return typeof(T).IsAssignableFrom(typeof(T4)) ? (T)(object)_v4! : base.Get<T>();
    }

    public static implicit operator T1(Union<T1, T2, T3, T4> union) => union.Get<T1>();

    public static implicit operator T2(Union<T1, T2, T3, T4> union) => union.Get<T2>();

    public static implicit operator T3(Union<T1, T2, T3, T4> union) => union.Get<T3>();

    public static implicit operator T4(Union<T1, T2, T3, T4> union) => union.Get<T4>();

    public static implicit operator Union<T1, T2, T3, T4>(T1 value) => new Union<T1, T2, T3, T4>(value);

    public static implicit operator Union<T1, T2, T3, T4>(T2 value) => new Union<T1, T2, T3, T4>(value);

    public static implicit operator Union<T1, T2, T3, T4>(T3 value) => new Union<T1, T2, T3, T4>(value);

    public static implicit operator Union<T1, T2, T3, T4>(T4 value) => new Union<T1, T2, T3, T4>(value);

    public override void Serialize(Utf8JsonWriter writer, JsonSerializerOptions options)
    {
        if (_type == typeof(T4))
        {
            JsonSerializer.Serialize(writer, _v4, options);
        }
        else
        {
            base.Serialize(writer, options);
        }
    }

    protected static new Union<T1, T2, T3, T4>? DeserializeInternal(string json, JsonSerializerOptions options)
    {
        if (Union<T1, T2, T3>.DeserializeInternal(json, options) is Union<T1, T2, T3> union)
        {
            return new Union<T1, T2, T3, T4>(union);
        }

        try
        {
            var value4 = JsonSerializer.Deserialize<T4>(json, options);
            return new Union<T1, T2, T3, T4>(value4!);
        }
        catch (JsonException) { }

        return null;
    }

    public static new Union<T1, T2, T3, T4> Deserialize(string json, JsonSerializerOptions options)
    {
        return DeserializeInternal(json, options) ?? throw new JsonException("Invalid JSON");
    }
}

public class UnionJsonConverter<T1, T2> : JsonConverter<Union<T1, T2>>
{
    public override Union<T1, T2>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var json = JsonSerializer.Deserialize<JsonElement>(ref reader).GetRawText();
        return json == null ? null : Union<T1, T2>.Deserialize(json, options);
    }

    public override void Write(Utf8JsonWriter writer, Union<T1, T2> value, JsonSerializerOptions options)
    {
        value.Serialize(writer, options);
    }
}

public class UnionJsonConverter<T1, T2, T3> : JsonConverter<Union<T1, T2, T3>>
{
    public override Union<T1, T2, T3>? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        var json = JsonSerializer.Deserialize<JsonElement>(ref reader).GetRawText();
        return json == null ? null : Union<T1, T2, T3>.Deserialize(json, options);
    }

    public override void Write(Utf8JsonWriter writer, Union<T1, T2, T3> value, JsonSerializerOptions options)
    {
        value.Serialize(writer, options);
    }
}

public class UnionJsonConverter<T1, T2, T3, T4> : JsonConverter<Union<T1, T2, T3, T4>>
{
    public override Union<T1, T2, T3, T4>? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        var json = JsonSerializer.Deserialize<JsonElement>(ref reader).GetRawText();
        return json == null ? null : Union<T1, T2, T3, T4>.Deserialize(json, options);
    }

    public override void Write(Utf8JsonWriter writer, Union<T1, T2, T3, T4> value, JsonSerializerOptions options)
    {
        value.Serialize(writer, options);
    }
}
