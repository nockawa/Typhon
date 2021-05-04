// unset

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Typhon.Engine
{
    public class DatabaseDefinitions
    {
        private Dictionary<string, DBComponentDefinition> _components;
        private Dictionary<string, DBObjectDefinition> _objects;

        public DatabaseDefinitions()
        {
            _components = new Dictionary<string, DBComponentDefinition>();
            _objects = new Dictionary<string, DBObjectDefinition>();
        }

        public IDBComponentDefinitionBuilder CreateComponentBuilder(string name) => new DBComponentDefinitionBuilder(this, name);

        public interface IDBComponentDefinitionBuilder
        {
            IDbComponentFieldDefinitionBuilder WithField(int fieldId, string name, FieldType type, int offset);
            void Build();
        }

        public interface IDbComponentFieldDefinitionBuilder : IDBComponentDefinitionBuilder
        {
            IDbComponentFieldDefinitionBuilder IsStatic();
            IDbComponentFieldDefinitionBuilder IsArray(int length);
        }

        class DBComponentDefinitionBuilder : IDBComponentDefinitionBuilder
        {
            private readonly DatabaseDefinitions _owner;
            protected readonly DBComponentDefinition _component;

            public DBComponentDefinitionBuilder(DatabaseDefinitions owner, string name)
            {
                _owner = owner;
                _component = new DBComponentDefinition(name);
            }

            protected DBComponentDefinitionBuilder(DatabaseDefinitions owner, DBComponentDefinition component)
            {
                _owner = owner;
                _component = component;
            }

            public IDbComponentFieldDefinitionBuilder WithField(int fieldId, string name, FieldType type, int offset)
            {
                return new DBComponentFieldDefinitionBuilder(_owner, _component, fieldId, name, type, offset);
            }

            public void Build()
            {
                _component.Build();
                _owner.AddComponent(_component);
            }
        }

        public void AddComponent(DBComponentDefinition component)
        {
            if (_components.ContainsKey(component.Name))
            {
                throw new ArgumentException($"The component name '{component.Name}' is already taken", nameof(component));
            }
            _components.Add(component.Name, component);
        }

        class DBComponentFieldDefinitionBuilder : DBComponentDefinitionBuilder, IDbComponentFieldDefinitionBuilder
        {
            private DBComponentDefinition.Field _field;

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
                FieldType fieldType, int offset) : base(owner, component)
            {
                DBComponentDefinition.Field.CheckName(fieldName);
                DBComponentDefinition.Field.CheckType(fieldType);
                _field = _component.CreateField(fieldId, fieldName, fieldType, offset);
            }

        }

        public DBComponentDefinition GetComponent(string componentName) => _components.TryGetValue(componentName, out var res) == false ? null : res;

        public DBComponentDefinition CreateFromRowAccessor<T>() where T : unmanaged
        {
            var t = typeof(T);

            var ca = t.GetCustomAttribute<ComponentAttribute>();
            var dbc = new DBComponentDefinition((ca != null) ? ca.Name : t.Name);

            if (_components.TryGetValue(dbc.Name, out _)) return null;

            var members = t.GetFields();
            var fieldId = 0;
            for (int i = 0; i < members.Length; i++)
            {
                var fieldInfo = members[i];

                if (fieldInfo.IsStatic) continue;

                var fa = fieldInfo.GetCustomAttribute<FieldAttribute>();
                var ia = fieldInfo.GetCustomAttribute<IndexAttribute>();

                var fieldType = DatabaseSchemaExtensions.FromType(fieldInfo.FieldType);
                if (fieldType != FieldType.None)
                {
                    // Name of the field is by default the C# member name, or the one specified by the FieldAttribute
                    var fieldName = fa?.Name ?? fieldInfo.Name;
                    var fieldOffset = Marshal.OffsetOf(t, fieldInfo.Name).ToInt32();

                    var field = dbc.CreateField(fa?.FieldId ?? fieldId++, fieldName, fieldType, fieldOffset);

                    // Index related data
                    if (ia != null)
                    {
                        field.HasIndex = true;
                        field.IndexAllowMultiple = ia.AllowMultiple;
                        field.IsIndexAuto = false;
                    }
                }
            }

            dbc.Build();

            _components.Add(dbc.Name, dbc);
            return dbc;
        }
    }
}