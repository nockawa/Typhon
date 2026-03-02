using System;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

public class QueryBuilder<T> where T : unmanaged
{
    private readonly DatabaseEngine _dbe;
    private Expression<Func<T, bool>> _predicate;

    internal QueryBuilder(DatabaseEngine dbe)
    {
        _dbe = dbe;
    }

    public QueryBuilder<T> Where(Expression<Func<T, bool>> predicate)
    {
        _predicate = predicate;
        return this;
    }

    public View<T> ToView(int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity)
    {
        if (_predicate == null)
        {
            throw new InvalidOperationException("A Where predicate must be specified before calling ToView().");
        }

        var fieldPredicates = ExpressionParser.Parse<T>(_predicate);
        var ct = _dbe.GetComponentTable<T>();
        if (ct == null)
        {
            throw new InvalidOperationException($"Component type {typeof(T).Name} is not registered.");
        }

        var evaluators = ResolveEvaluators(fieldPredicates, ct);
        var view = new View<T>(evaluators, ct.ViewRegistry, bufferCapacity);

        // Register before population so concurrent commits go to ring buffer
        ct.ViewRegistry.RegisterView(view);

        // Populate initial entity set via PK index scan
        using var tx = _dbe.CreateQuickTransaction();
        var pkIndex = ct.PrimaryKeyIndex;

        foreach (var kv in pkIndex.EnumerateLeaves())
        {
            if (tx.ReadEntity<T>(kv.Key, out var comp))
            {
                if (EvaluateAll(evaluators, ref comp))
                {
                    view.AddEntityDirect(kv.Key);
                }
            }
        }

        // Drain any concurrent entries that arrived during population
        view.Refresh(tx);
        // Discard baseline artifacts
        view.ClearDelta();

        return view;
    }

    private static unsafe bool EvaluateAll(FieldEvaluator[] evaluators, ref T comp)
    {
        var compPtr = (byte*)Unsafe.AsPointer(ref comp);
        for (var i = 0; i < evaluators.Length; i++)
        {
            ref var eval = ref evaluators[i];
            if (!FieldEvaluator.Evaluate(ref eval, compPtr + eval.FieldOffset))
            {
                return false;
            }
        }
        return true;
    }

    private static FieldEvaluator[] ResolveEvaluators(FieldPredicate[] predicates, ComponentTable ct) =>
        QueryResolverHelper.ResolveEvaluators(predicates, ct, 0);
}

internal static class QueryResolverHelper
{
    public static FieldEvaluator[] ResolveEvaluators(FieldPredicate[] predicates, ComponentTable ct, byte componentTag)
    {
        var definition = ct.Definition;
        var evaluators = new FieldEvaluator[predicates.Length];

        for (var i = 0; i < predicates.Length; i++)
        {
            var pred = predicates[i];

            if (!definition.FieldsByName.TryGetValue(pred.FieldName, out var field))
            {
                throw new InvalidOperationException($"Field '{pred.FieldName}' not found on component '{definition.Name}'.");
            }

            if (!field.HasIndex)
            {
                throw new InvalidOperationException($"Field '{pred.FieldName}' is not indexed. View predicates require indexed fields.");
            }

            var fieldIndex = FindFieldIndex(definition, field);
            var keyType = MapFieldTypeToKeyType(field.Type);
            var threshold = EncodeThreshold(pred.Value, keyType);

            evaluators[i] = new FieldEvaluator
            {
                FieldIndex = fieldIndex,
                FieldOffset = field.OffsetInComponentStorage,
                FieldSize = (byte)field.FieldSize,
                KeyType = keyType,
                CompareOp = pred.Operator,
                Threshold = threshold,
                ComponentTag = componentTag
            };
        }

        return evaluators;
    }

    /// <summary>
    /// Finds the index into IndexedFieldInfos[] by replicating the iteration order from BuildIndexedFieldInfo.
    /// </summary>
    public static int FindFieldIndex(DBComponentDefinition definition, DBComponentDefinition.Field targetField)
    {
        var index = 0;
        for (var i = 0; i < definition.MaxFieldId; i++)
        {
            var f = definition[i];
            if (f == null || !f.HasIndex)
            {
                continue;
            }

            if (f == targetField)
            {
                return index;
            }

            index++;
        }

        throw new InvalidOperationException($"Field '{targetField.Name}' not found in indexed fields.");
    }

    public static KeyType MapFieldTypeToKeyType(FieldType fieldType) =>
        fieldType switch
        {
            FieldType.Boolean => KeyType.Bool,
            FieldType.Byte => KeyType.SByte,      // FieldType.Byte = signed byte (sbyte)
            FieldType.UByte => KeyType.Byte,       // FieldType.UByte = unsigned byte (byte)
            FieldType.Short => KeyType.Short,
            FieldType.UShort => KeyType.UShort,
            FieldType.Int => KeyType.Int,
            FieldType.UInt => KeyType.UInt,
            FieldType.Long => KeyType.Long,
            FieldType.ULong => KeyType.ULong,
            FieldType.Float => KeyType.Float,
            FieldType.Double => KeyType.Double,
            _ => throw new NotSupportedException($"Field type {fieldType} is not supported for view predicates.")
        };

    public static long EncodeThreshold(object value, KeyType keyType)
    {
        switch (keyType)
        {
            case KeyType.Bool:
                return Convert.ToBoolean(value) ? 1L : 0L;
            case KeyType.SByte:
                return Convert.ToSByte(value);
            case KeyType.Byte:
                return (long)(ulong)Convert.ToByte(value);
            case KeyType.Short:
                return Convert.ToInt16(value);
            case KeyType.UShort:
                return (long)(ulong)Convert.ToUInt16(value);
            case KeyType.Int:
                return Convert.ToInt32(value);
            case KeyType.UInt:
                return (long)(ulong)Convert.ToUInt32(value);
            case KeyType.Long:
                return Convert.ToInt64(value);
            case KeyType.ULong:
                return (long)Convert.ToUInt64(value);
            case KeyType.Float:
            {
                var f = Convert.ToSingle(value);
                return (long)Unsafe.As<float, int>(ref f);
            }
            case KeyType.Double:
            {
                var d = Convert.ToDouble(value);
                return Unsafe.As<double, long>(ref d);
            }
            default:
                throw new NotSupportedException($"Key type {keyType} is not supported.");
        }
    }
}
