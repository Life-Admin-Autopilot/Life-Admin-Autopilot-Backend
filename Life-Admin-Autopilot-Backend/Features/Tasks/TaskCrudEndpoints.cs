using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Tasks;
using Life_Admin_Autopilot.BLL.Kernel.Mappers;
using Life_Admin_Autopilot.BLL.Kernel.Tasks;
using Life_Admin_Autopilot.DAL.Features.Tasks;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Features.Tasks.Binding;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Life_Admin_Autopilot_Backend.Kernel.Binding;
using MongoDB.Bson;

namespace Life_Admin_Autopilot_Backend.Features.Tasks;

/// <summary>Create, read one, patch and soft-delete.</summary>
internal static class TaskCrudEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/me/tasks", async (
            HttpContext ctx,
            TaskWriteService writes,
            CancellationToken ct) =>
        {
            var user = ctx.RequireUser();
            var body = await KernelBody
                .ReadAsync<CreateTaskBody>(ctx, KernelBodyOptions.Strict_("Invalid task payload."), ct)
                .ConfigureAwait(false);

            var f = new BodyFields();

            var title = f.TrimmedString(body.Title, "title", min: 1, max: 240, required: true);
            var domain = f.Enum(body.Domain, "domain", TaskVocabulary.Domains, required: true);
            var kind = f.Enum(body.Kind, "kind", TaskVocabulary.Kinds, required: false);
            var priority = f.Enum(body.Priority, "priority", TaskVocabulary.Priorities, required: false);
            var tags = TaskFieldReaders.Tags(f, body.Tags);
            var dueAt = f.IsoDate(body.DueAt, "dueAt");
            var notes = f.PlainString(body.Notes, "notes", max: 2000);
            var estimate = TaskFieldReaders.Estimate(f, body.Estimate);
            var amount = TaskFieldReaders.Amount(f, body.Amount);
            var sourceVoiceNoteId = TaskFieldReaders.ObjectIdString(f, body.SourceVoiceNoteId, "sourceVoiceNoteId");

            f.ThrowIfInvalid("invalid_body", "Invalid task payload.");

            var task = await writes
                .CreateAsync(
                    user.Id,
                    new TaskCreateInput(
                        title!,
                        domain!,
                        kind,
                        priority,
                        tags,
                        dueAt,
                        notes,
                        estimate,
                        amount,
                        sourceVoiceNoteId),
                    cancellationToken: ct)
                .ConfigureAwait(false);

            return Results.Created((string?)null, new { task = task.ToDto() });
        }).RequireAuthorization();

        endpoints.MapGet("/me/tasks/{id}", async (
            string id,
            HttpContext ctx,
            TaskRepository repository,
            UserLocaleReader locales,
            CancellationToken ct) =>
        {
            var user = ctx.RequireUser();
            var taskId = TaskEndpoints.TaskId(id);

            var task = await repository.FindLiveAsync(user.Id, taskId, ct).ConfigureAwait(false)
                ?? throw TaskEndpoints.TaskNotFound();

            var locale = await locales.ReadAsync(user.Id, ct).ConfigureAwait(false);

            // One of exactly TWO endpoints that apply the i18n overlay and strip the
            // `i18n` field. Everything else ships the raw toJSON.
            return Results.Ok(new { task = MatterLocale.Present(task, locale) });
        }).RequireAuthorization();

        endpoints.MapPatch("/me/tasks/{id}", async (
            string id,
            HttpContext ctx,
            TaskWriteService writes,
            CancellationToken ct) =>
        {
            var user = ctx.RequireUser();
            var taskId = TaskEndpoints.TaskId(id);

            var body = await KernelBody
                .ReadAsync<UpdateTaskBody>(ctx, KernelBodyOptions.Strict_("Invalid task update."), ct)
                .ConfigureAwait(false);

            var f = new BodyFields();
            var patch = new BsonDocument();

            // Non-nullable fields: present means a value, and an explicit null is a
            // type error rather than a clear.
            TaskFieldReaders.SetIfPresent(patch, "title", body.Title, f, (el, fields) =>
                fields.TrimmedString(el, "title", min: 1, max: 240, required: true) is { } v ? v : BsonNull.Value);

            TaskFieldReaders.SetIfPresent(patch, "domain", body.Domain, f, (el, fields) =>
                fields.Enum(el, "domain", TaskVocabulary.Domains, required: true) is { } v ? v : BsonNull.Value);

            TaskFieldReaders.SetIfPresent(patch, "status", body.Status, f, (el, fields) =>
                fields.Enum(el, "status", TaskVocabulary.Statuses, required: true) is { } v ? v : BsonNull.Value);

            TaskFieldReaders.SetIfPresent(patch, "priority", body.Priority, f, (el, fields) =>
                fields.Enum(el, "priority", TaskVocabulary.Priorities, required: true) is { } v ? v : BsonNull.Value);

            TaskFieldReaders.SetIfPresent(patch, "tags", body.Tags, f, (el, fields) =>
                TaskFieldReaders.Tags(fields, el) is { } v ? new BsonArray(v) : BsonNull.Value);

            // Nullable fields: an explicit null CLEARS ($unset), an omitted key
            // leaves the value alone. This is the single most common port bug,
            // because most serialisers cannot tell the two apart.
            TaskFieldReaders.SetNullable(patch, "dueAt", body.DueAt, f, (el, fields) =>
                fields.IsoDate(el, "dueAt") is { } v ? v : BsonNull.Value);

            TaskFieldReaders.SetNullable(patch, "notes", body.Notes, f, (el, fields) =>
                fields.PlainString(el, "notes", max: 2000) is { } v ? v : BsonNull.Value);

            TaskFieldReaders.SetNullable(patch, "estimate", body.Estimate, f, (el, fields) =>
                TaskFieldReaders.Estimate(fields, el) is { } v
                    ? new BsonDocument
                    {
                        ["minMinutes"] = v.MinMinutes,
                        ["maxMinutes"] = v.MaxMinutes,
                        ["source"] = v.Source,
                    }
                    : BsonNull.Value);

            // `source` is written here, never read from the body — a patched amount
            // is a person's figure by definition, and stamping it 'user' is what
            // stops a later AI pass overwriting what they typed.
            TaskFieldReaders.SetNullable(patch, "amount", body.Amount, f, (el, fields) =>
                TaskFieldReaders.Amount(fields, el) is { } v
                    ? new BsonDocument
                    {
                        ["amountMinor"] = v.AmountMinor,
                        ["currency"] = v.Currency,
                        ["source"] = v.Source,
                        ["direction"] = v.Direction,
                    }
                    : BsonNull.Value);

            TaskFieldReaders.SetNullable(patch, "snoozedUntil", body.SnoozedUntil, f, (el, fields) =>
                fields.IsoDate(el, "snoozedUntil") is { } v ? v : BsonNull.Value);

            f.ThrowIfInvalid("invalid_body", "Invalid task update.");

            var task = await writes.PatchAsync(user.Id, taskId, patch, cancellationToken: ct).ConfigureAwait(false)
                ?? throw TaskEndpoints.TaskNotFound();

            return Results.Ok(new { task = task.ToDto() });
        }).RequireAuthorization();

        endpoints.MapDelete("/me/tasks/{id}", async (
            string id,
            HttpContext ctx,
            BulkService bulk,
            CancellationToken ct) =>
        {
            var user = ctx.RequireUser();
            var taskId = TaskEndpoints.TaskId(id);

            // Routed through the bulk service so a single swipe-delete produces the
            // same TaskBulkOp record — and therefore the same one-tap undo — as a
            // date-range wipe. ONE delete path, one undo path.
            var result = await bulk
                .ApplyAsync(
                    user.Id,
                    new BulkTarget { Ids = new[] { taskId.ToString() } },
                    new BulkActionInput.Delete(),
                    cancellationToken: ct)
                .ConfigureAwait(false);

            if (result.Affected == 0)
            {
                throw TaskEndpoints.TaskNotFound();
            }

            return Results.Ok(new TaskDeleteResponse { UndoToken = result.UndoToken });
        }).RequireAuthorization();
    }
}

/// <summary>Field readers shared by create and patch.</summary>
internal static class TaskFieldReaders
{
    /// <summary>
    /// Accepts raw user-input tags, normalises to lowercase-kebab, drops empties
    /// and duplicates, and caps at <c>MAX_TAGS</c> — extras are dropped SILENTLY.
    /// The array itself is capped at <c>MAX_TAGS * 2</c> by the schema, and THAT
    /// one is an error.
    /// </summary>
    public static List<string>? Tags(BodyFields f, JsonElement element)
    {
        var raw = f.StringArray(element, "tags", itemMax: 64, arrayMax: TaskVocabulary.MaxTags * 2);
        if (raw is null)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<string>();

        foreach (var candidate in raw)
        {
            var tag = TaskVocabulary.NormalizeTag(candidate);
            if (tag is null || !seen.Add(tag))
            {
                continue;
            }

            output.Add(tag);
            if (output.Count >= TaskVocabulary.MaxTags)
            {
                break;
            }
        }

        return output;
    }

    /// <summary>
    /// A hand-set time window, snapped onto the bucket ladder like any other
    /// estimate and stamped <c>source: 'user'</c> — which is what makes it
    /// authoritative forever.
    ///
    /// <para>Issues are keyed under <c>"estimate"</c>, not <c>"estimate.minMinutes"</c>,
    /// because zod's <c>flatten()</c> buckets by <c>issue.path[0]</c>.</para>
    /// </summary>
    public static TaskEstimateDocument? Estimate(BodyFields f, JsonElement element)
    {
        if (!BodyFields.HasValue(element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            f.AddIssue("estimate", ZodMessages.ExpectedType("object", element.ValueKind.ToString().ToLowerInvariant()));
            return null;
        }

        var unknown = element
            .EnumerateObject()
            .Select(p => p.Name)
            .Where(name => name is not ("minMinutes" or "maxMinutes"))
            .ToList();

        if (unknown.Count > 0)
        {
            f.AddIssue("estimate", ZodMessages.UnrecognizedKeys(unknown));
            return null;
        }

        var min = f.Int(Member(element, "minMinutes"), "estimate", min: 1, max: 1440, required: true);
        var max = f.Int(Member(element, "maxMinutes"), "estimate", min: 1, max: 1440, required: true);

        if (min is null || max is null)
        {
            return null;
        }

        return EstimateNormalizer.Normalize(min, max, "user");
    }

    /// <summary>
    /// An amount, in either of the two shapes the domain already has gates for:
    /// <c>{ amountMinor, currency, direction? }</c> — what a PERSON typed, counted
    /// in minor units by the app — or <c>{ amountMajor, currency, direction? }</c>
    /// — a figure an EXTRACTOR heard or read, as printed or spoken.
    ///
    /// <para>
    /// <b><c>source</c> is not readable from the body</b> and is never taken from
    /// it. It is the flag that says a person stood behind this figure — it decides
    /// whether the UI owes a provenance chip, and it is what makes the value
    /// authoritative against any later AI pass. A client able to set it could
    /// launder a guess into a fact.
    /// </para>
    ///
    /// <para>
    /// So the SHAPE decides it, and the shapes cannot be confused for each other.
    /// The app has always sent minor units — it computes them with the same ISO
    /// exponent table this server uses — and only a model ever says "500 EGP",
    /// because a model has no exponent table and must not be asked to guess one.
    /// A body carrying BOTH keys is rejected rather than resolved by precedence:
    /// it can only be a caller that does not know which of the two it is.
    /// </para>
    ///
    /// <para>
    /// A bad currency <b>rejects the request</b> rather than silently dropping the
    /// amount the way the extractor does. The asymmetry is deliberate: the
    /// extractor is guessing and a dropped guess costs nothing, but a person who
    /// typed 480.00 and got a matter with no amount would have to notice the
    /// silence to know it failed.
    /// </para>
    ///
    /// <para>Issues are keyed under <c>"amount"</c> — zod's <c>flatten()</c>
    /// buckets by <c>issue.path[0]</c>, same as the estimate above.</para>
    /// </summary>
    public static MoneyDocument? Amount(BodyFields f, JsonElement element)
    {
        if (!BodyFields.HasValue(element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            f.AddIssue("amount", ZodMessages.ExpectedType("object", element.ValueKind.ToString().ToLowerInvariant()));
            return null;
        }

        var unknown = element
            .EnumerateObject()
            .Select(p => p.Name)
            // `source` is listed as unknown on purpose: a client that sends it gets
            // a clear rejection rather than having it quietly ignored.
            .Where(name => name is not ("amountMinor" or "amountMajor" or "currency" or "direction"))
            .ToList();

        if (unknown.Count > 0)
        {
            f.AddIssue("amount", ZodMessages.UnrecognizedKeys(unknown));
            return null;
        }

        var minorElement = Member(element, "amountMinor");
        var majorElement = Member(element, "amountMajor");
        var hasMinor = BodyFields.HasValue(minorElement);
        var hasMajor = BodyFields.HasValue(majorElement);

        if (hasMinor && hasMajor)
        {
            f.AddIssue("amount", "send either amountMinor or amountMajor, never both");
            return null;
        }

        if (!hasMinor && !hasMajor)
        {
            f.AddIssue("amount", "amount requires amountMinor (from the app) or amountMajor (from an extractor)");
            return null;
        }

        var currencyRaw = f.TrimmedString(Member(element, "currency"), "amount", min: 3, max: 3, required: true);
        var direction = f.Enum(Member(element, "direction"), "amount", MoneyVocabulary.Directions, required: false);
        var currency = currencyRaw is null ? null : MoneyVocabulary.NormalizeCurrency(currencyRaw);

        if (currencyRaw is not null && currency is null)
        {
            f.AddIssue("amount", "currency must be a three-letter ISO 4217 code");
            return null;
        }

        if (hasMajor)
        {
            // Bounded before it reaches the gate for the same reason the gate bounds
            // it again: a figure past the ceiling is a misread separator, and
            // `decimal` throws rather than saturating if it is scaled first.
            var major = f.Decimal(
                majorElement,
                "amount",
                min: 0m,
                max: MoneyVocabulary.MaxMajorUnits,
                required: true);

            if (major is null || currency is null) return null;

            var heard = MoneyVocabulary.Normalize(major, currency, "ai", direction);
            if (heard is null)
            {
                f.AddIssue("amount", "amountMajor is out of range");
            }

            return heard;
        }

        // The ceiling is the money gate's own, expressed in minor units, so this
        // and MoneyVocabulary.FromMinor cannot disagree about what is too large.
        var minor = f.Long(
            minorElement,
            "amount",
            min: 0,
            max: (long)MoneyVocabulary.MaxMajorUnits * 1000,
            required: true);

        if (minor is null || currency is null)
        {
            return null;
        }

        var money = MoneyVocabulary.FromMinor(minor, currency, "user", direction);
        if (money is null)
        {
            f.AddIssue("amount", "amountMinor is out of range");
        }

        return money;
    }

    /// <summary><c>z.string().refine(ObjectId.isValid, 'invalid_object_id')</c>.</summary>
    public static ObjectId? ObjectIdString(BodyFields f, JsonElement element, string field)
    {
        if (!BodyFields.HasValue(element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            f.AddIssue(field, ZodMessages.ExpectedType("string", element.ValueKind.ToString().ToLowerInvariant()));
            return null;
        }

        if (!ObjectId.TryParse(element.GetString(), out var parsed))
        {
            f.AddIssue(field, "invalid_object_id");
            return null;
        }

        return parsed;
    }

    /// <summary>
    /// A field with no nullable form: an explicit <c>null</c> falls through to the
    /// reader, which reports the type error zod would.
    /// </summary>
    public static void SetIfPresent(
        BsonDocument patch,
        string field,
        JsonElement element,
        BodyFields f,
        Func<JsonElement, BodyFields, BsonValue> read)
    {
        if (BodyFields.IsAbsent(element))
        {
            return;
        }

        var value = read(element, f);
        if (!value.IsBsonNull)
        {
            patch[field] = value;
        }
    }

    /// <summary>
    /// A nullable field: an explicit <c>null</c> is recorded as BSON null, which
    /// <c>BulkService.ToMongoOps</c> turns into <c>$unset</c>.
    /// </summary>
    public static void SetNullable(
        BsonDocument patch,
        string field,
        JsonElement element,
        BodyFields f,
        Func<JsonElement, BodyFields, BsonValue> read)
    {
        if (BodyFields.IsAbsent(element))
        {
            return;
        }

        if (BodyFields.IsNull(element))
        {
            patch[field] = BsonNull.Value;
            return;
        }

        var value = read(element, f);
        if (!value.IsBsonNull)
        {
            patch[field] = value;
        }
    }

    private static JsonElement Member(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) ? value : default;
}
