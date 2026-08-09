using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;

namespace Life_Admin_Autopilot.DAL.Kernel.Mongo;

/// <summary>
/// Registered exactly once, at startup, before any collection is resolved.
///
/// <list type="bullet">
///   <item>camelCase element names — Mongoose writes camelCase, C# properties are PascalCase.</item>
///   <item>Ignore extra elements — a document written by the Node server may carry
///         fields this build does not model yet; throwing on them would make every
///         read of a live row fail.</item>
///   <item>Ignore null values when writing — Mongoose omits <c>undefined</c> fields
///         rather than storing <c>null</c>, and <c>{ $exists: false }</c> filters
///         (see <c>NotDeleted()</c>) depend on the difference.</item>
///   <item>DateTime always round-trips as UTC.</item>
/// </list>
/// </summary>
public static class MongoKernelConventions
{
    private const string PackName = "kitto-kernel";
    private static readonly object Gate = new();
    private static bool _registered;

    public static void Register()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            var pack = new ConventionPack
            {
                new CamelCaseElementNameConvention(),
                new IgnoreExtraElementsConvention(true),
                new IgnoreIfNullConvention(true),
            };
            ConventionRegistry.Register(PackName, pack, _ => true);

            BsonSerializer.RegisterSerializer(
                new DateTimeSerializer(DateTimeKind.Utc, BsonType.DateTime));
            BsonSerializer.RegisterSerializer(
                new NullableSerializer<DateTime>(new DateTimeSerializer(DateTimeKind.Utc, BsonType.DateTime)));

            _registered = true;
        }
    }
}
