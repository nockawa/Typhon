// unset

using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

[PublicAPI]
public class DatabaseDefinitions
{
    private readonly Dictionary<string, DBComponentDefinition> _components;
    private Dictionary<string, DBObjectDefinition> _objects;

    public int ComponentCount => _components.Count;
    public IEnumerable<string> ComponentNames => _components.Keys;

    public DatabaseDefinitions()
    {
        _components = new Dictionary<string, DBComponentDefinition>();
        _objects = new Dictionary<string, DBObjectDefinition>();
    }

    public IDBComponentDefinitionBuilder CreateComponentBuilder(string name, int revision) => new DBComponentDefinitionBuilder(this, name, revision);

    public interface IDBComponentDefinitionBuilder
    {
        IDbComponentFieldDefinitionBuilder WithField<T>(int fieldId, string name, int offset) where T : unmanaged;
        void Build();
        IDBComponentDefinitionBuilder WithPOCO<T>();
    }

    public interface IDbComponentFieldDefinitionBuilder : IDBComponentDefinitionBuilder
    {
        IDbComponentFieldDefinitionBuilder IsStatic();
        IDbComponentFieldDefinitionBuilder IsArray(int length);
    }

    class DBComponentDefinitionBuilder : IDBComponentDefinitionBuilder
    {
        private readonly DatabaseDefinitions _owner;
        protected readonly DBComponentDefinition Component;

        public DBComponentDefinitionBuilder(DatabaseDefinitions owner, string name, int revision, bool allowMultiple = false)
        {
            _owner = owner;
            Component = new DBComponentDefinition(name, revision, allowMultiple);
        }

        protected DBComponentDefinitionBuilder(DatabaseDefinitions owner, DBComponentDefinition component)
        {
            _owner = owner;
            Component = component;
        }

        public IDBComponentDefinitionBuilder WithPOCO<T>()
        {
            Component.POCOType = typeof(T);
            return this;
        }

        public IDbComponentFieldDefinitionBuilder WithField<T>(int fieldId, string name, int offset) where T : unmanaged 
            => new DBComponentFieldDefinitionBuilder(_owner, Component, fieldId, name, typeof(T), offset);

        public void Build()
        {
            Component.Build();
            _owner.AddComponent(Component);
        }
    }

    public void AddComponent(DBComponentDefinition component)
    {
        if (!_components.TryAdd(component.FullName, component))
        {
            throw new ArgumentException($"The component name '{component.Name}' is already taken", nameof(component));
        }
    }

    class DBComponentFieldDefinitionBuilder : DBComponentDefinitionBuilder, IDbComponentFieldDefinitionBuilder
    {
        private readonly DBComponentDefinition.Field _field;

        public IDbComponentFieldDefinitionBuilder IsStatic()
        {
            _field.IsStatic = true;
            return this;
        }

        public IDbComponentFieldDefinitionBuilder IsArray(int length)
        {
            _field.ArrayLength = length;
            return this;
        }

        internal DBComponentFieldDefinitionBuilder(DatabaseDefinitions owner, DBComponentDefinition component, int fieldId, string fieldName,
            Type type, int offset) : base(owner, component)
        {
            var (fieldType, underType) = DatabaseSchemaExtensions.FromType(type);
            DBComponentDefinition.Field.CheckName(fieldName);
            DBComponentDefinition.Field.CheckType(fieldType);
            _field = Component.CreateField(fieldId, fieldName, fieldType, underType, offset, type);
        }

    }

    public DBComponentDefinition GetComponent(string componentName, int revision) => _components.GetValueOrDefault(DBComponentDefinition.FormatFullName(componentName, revision));

    public DBComponentDefinition CreateFromAccessor<T>() where T : unmanaged
    {
        var t = typeof(T);

        var ca = t.GetCustomAttribute<ComponentAttribute>();
        if (ca == null)
        {
            throw new InvalidOperationException($"Missing the ComponentAttribute on the type {t} declaration");
        }
        
        var compDef = new DBComponentDefinition(ca.Name ?? t.Name, ca.Revision, ca.AllowMultiple) { POCOType = t };

        if (_components.TryGetValue(compDef.FullName, out _))
        {
            return null;
        }

        var members = t.GetFields();
        var fieldId = 0;
        foreach (var fieldInfo in members)
        {
            if (fieldInfo.IsStatic)
            {
                continue;
            }

            var fa = fieldInfo.GetCustomAttribute<FieldAttribute>();
            var ia = fieldInfo.GetCustomAttribute<IndexAttribute>();

            var (fieldType, fieldUnderlyingType) = DatabaseSchemaExtensions.FromType(fieldInfo.FieldType);
            if (fieldType == FieldType.None)
            {
                continue;
            }

            // Name of the field is by default the C# member name, or the one specified by the FieldAttribute
            var fieldName = fa?.Name ?? fieldInfo.Name;
            var fieldOffset = Marshal.OffsetOf(t, fieldInfo.Name).ToInt32();

            var field = compDef.CreateField(fa?.FieldId ?? fieldId++, fieldName, fieldType, fieldUnderlyingType, fieldOffset, fieldInfo.FieldType);

            // Index related data
            if (ia == null)
            {
                continue;
            }

            field.HasIndex = true;
            field.IndexAllowMultiple = ia.AllowMultiple;
            field.IsIndexAuto = false;
        }

        compDef.Build();

        _components.Add(compDef.FullName, compDef);
        return compDef;
    }
}