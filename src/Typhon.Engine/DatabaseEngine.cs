using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using System.Runtime.CompilerServices;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

[assembly: InternalsVisibleTo("Typhon.Engine.Tests")]

namespace Typhon.Engine
{
    public class DatabaseEngine : IDisposable
    {
        private readonly DatabaseConfiguration _configuration;
        private readonly PersistentDataAccess _persistentDataAccess;

        private bool _isDisposed;

        public DatabaseEngine(IConfiguration<DatabaseConfiguration> dc, PersistentDataAccess pda)
        {
            _configuration = dc.Value;
            _persistentDataAccess = pda;

            // Check the configuration
            _configuration.Validate(false, out _);


        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
        }
    }

    public class TimeManager
    {
        public static TimeManager Singleton { get; internal set; }

        public int ExecutionFrame { get; private set; }

        public TimeManager()
        {
            Singleton = this;
            ExecutionFrame = 1;
        }

        internal void BumpFrame() => ++ExecutionFrame;
    }

    public static class LogExtensions
    {
        public static LoggerConfiguration WithCurrentFrame(this LoggerEnrichmentConfiguration enrichmentConfiguration) =>
            enrichmentConfiguration != null ? enrichmentConfiguration.With<CurrentFrameEnricher>() : throw new ArgumentNullException(nameof (enrichmentConfiguration));
    }

    public class CurrentFrameEnricher : ILogEventEnricher
    {
        /// <summary>The property name added to enriched log events.</summary>
        public const string ThreadIdPropertyName = "ThreadId";

        /// <summary>Enrich the log event.</summary>
        /// <param name="logEvent">The log event to enrich.</param>
        /// <param name="propertyFactory">Factory for creating new properties to add to the event.</param>
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) => logEvent.AddPropertyIfAbsent(new LogEventProperty("CurrentFrame", (LogEventPropertyValue) new ScalarValue((object) TimeManager.Singleton.ExecutionFrame)));
    }


    public enum FieldType
    {
        None       = 0,
        Boolean    = 1,
        Byte       = 2,
        Short      = 3,
        Int        = 4,
        Long       = 5,
        UByte      = 256+2,
        UShort     = 256+3,
        UInt       = 256+4,
        ULong      = 256+5,
        Float      = 6,
        Double     = 7,
        Char       = 8,
        String     = 9,
        String64   = 10,
        String1024 = 11,
        Point2f    = 12,
        Point3f    = 13,
        Point4f    = 14,
        Point2d    = 512+12,
        Point3d    = 512+13,
        Point4d    = 512+14,
        Quaternion = 11
    }

    public class DBComponentDefinition
    {
        public string Name { get; set; }

        private Dictionary<string, Field> _fields;
        public IReadOnlyDictionary<string, Field> Fields => _fields;

        public class Field
        {
            public Field(string name, FieldType type)
            {
                Name = name;
                Type = type;
            }

            public string Name { get; }
            public FieldType Type { get; }
            public bool IsReadOnly { get; set; }
            public bool IsStatic { get; set; }
            public bool IsUnique { get; set; }
            public bool HasIndex { get; set; }
            public bool IsArray => ArrayLength > 0;
            public uint ArrayLength { get; set; }

            public static void CheckName(string fieldName)
            {
                if (string.IsNullOrWhiteSpace(fieldName) || new Regex("^[A-Za-z]+$").IsMatch(fieldName) == false)
                {
                    throw new ArgumentException("Field name must be a single word of UTF8 size not exceeding 64 bytes", nameof(fieldName));
                }
                if (Encoding.UTF8.GetByteCount(fieldName) > 63)
                {
                    throw new ArgumentException($"The given field name '{fieldName}' is exceeding the size limit of 64 bytes", nameof(fieldName));
                }
            }

            public static void CheckType(FieldType fieldType)
            {
                if (Enum.IsDefined(fieldType) == false || fieldType==FieldType.None)
                {
                    throw new ArgumentException($"The given field type is not valid");
                }
            }
        }

        internal DBComponentDefinition(string name)
        {
            Name = name;
            _fields = new Dictionary<string, Field>();
        }

        public Field CreateField(string name, FieldType type)
        {
            if (_fields.ContainsKey(name))
            {
                throw new ArgumentException($"The field name '{name}' is already taken", nameof(name));
            }
            var field = new Field(name, type);
            _fields.Add(name, field);
            return field;
        }
    }

    public class DBObjectDefinition
    {
        public string Name { get; }
        public IReadOnlyList<DBComponentDefinition> Components { get; }

        internal DBObjectDefinition(string name)
        {
            Name = name;
            Components = new List<DBComponentDefinition>();
        }
    }

    public class DatabaseDefinitions
    {
        private Dictionary<string, DBComponentDefinition> _components;
        private Dictionary<string, DBObjectDefinition> _objects;

        public DatabaseDefinitions()
        {
            _components = new Dictionary<string, DBComponentDefinition>();
            _objects = new Dictionary<string, DBObjectDefinition>();
        }

        public IDBComponentDefinitionBuilder CreateComponentBuilder(string name) => new IdbComponentDefinitionBuilder(this, name);

        public interface IDBComponentDefinitionBuilder
        {
            IDbComponentFieldDefinitionBuilder WithField(string name, FieldType type);
            void Build();
        }

        public interface IDbComponentFieldDefinitionBuilder : IDBComponentDefinitionBuilder
        {
            IDbComponentFieldDefinitionBuilder IsReadOnly();
            IDbComponentFieldDefinitionBuilder IsStatic();
            IDbComponentFieldDefinitionBuilder IsArray(uint length);
        }

        class IdbComponentDefinitionBuilder : IDBComponentDefinitionBuilder
        {
            private readonly DatabaseDefinitions _owner;
            protected readonly DBComponentDefinition _component;

            public IdbComponentDefinitionBuilder(DatabaseDefinitions owner, string name)
            {
                _owner = owner;
                _component = new DBComponentDefinition(name);
            }

            protected IdbComponentDefinitionBuilder(DatabaseDefinitions owner, DBComponentDefinition component)
            {
                _owner = owner;
                _component = component;
            }

            public IDbComponentFieldDefinitionBuilder WithField(string name, FieldType type)
            {
                return new IdbComponentFieldDefinitionBuilder(_owner, _component, name, type);
            }

            public void Build()
            {
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

        class IdbComponentFieldDefinitionBuilder : IdbComponentDefinitionBuilder, IDbComponentFieldDefinitionBuilder
        {
            private DBComponentDefinition.Field _field;

            public IDbComponentFieldDefinitionBuilder IsReadOnly()
            {
                _field.IsReadOnly = true;
                return this;
            }

            public IDbComponentFieldDefinitionBuilder IsStatic()
            {
                _field.IsStatic = true;
                return this;
            }

            public IDbComponentFieldDefinitionBuilder IsArray(uint length)
            {
                _field.ArrayLength = length;
                return this;
            }

            internal IdbComponentFieldDefinitionBuilder(DatabaseDefinitions owner, DBComponentDefinition component, string fieldName, FieldType fieldType) : base(owner, component)
            {
                DBComponentDefinition.Field.CheckName(fieldName);
                DBComponentDefinition.Field.CheckType(fieldType);
                _field = _component.CreateField(fieldName, fieldType);
            }

        }
    }
}
