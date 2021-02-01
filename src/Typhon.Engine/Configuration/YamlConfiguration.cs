using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.Serialization.ObjectGraphVisitors;
using YamlDotNet.Serialization.TypeInspectors;

namespace Typhon.Engine
{
    /// <summary>
    /// Type storing the configuration settings of the Engine
    /// </summary>
    public class YamlConfiguration
    {

        /// <summary>
        /// Name of the database, should be a single word, no whitespace.
        /// </summary>
        /// <remarks>
        /// The default value is 'Database'.
        /// </remarks>
        [Description("The name of the database, should be a single word, no whitespace. Default value is 'Database'.")]
        [DefaultValue("Database")]
        public string DatabaseName { get; set; } = "Database";

        /// <summary>
        /// Directory where the database binary files are/should be stored
        /// </summary>
        /// <remarks>
        /// Can be relative to current directory or absolute. Current directory is used if not specified.
        /// </remarks>
        [Description("Directory where the database binary files are/should be stored, can be relative to current directory or absolute. Current directory is used if not specified")]
        public string DatabaseDirectory { get; set; }

        /// <summary>
        /// The prefix string to use for the database files to generate. If not specified the database name will be used.
        /// </summary>
        [Description("The prefix string to use for the database files to generate. If not specified the database name will be used.")]
        public string DatabaseFileName { get; set; }

        /// <summary>
        /// Generate a default configuration object.
        /// </summary>
        /// <remarks>You can change the configuration by setting properties then call <see cref="Validate"/> to check if the configuration is valid.</remarks>
        /// <returns>The configuration object.</returns>
        public static YamlConfiguration GenerateDefault() => new YamlConfiguration();

        /// <summary>
        /// Determine whether or not the configuration object is valid
        /// </summary>
        [YamlIgnore]
        public bool IsValid
        {
            get => Validate(true, out _);
        }

        public DatabaseConfiguration ToDatabaseConfiguration()
        {
            return new DatabaseConfiguration
            {
                DatabaseName = DatabaseName,
                DatabaseDirectory = DatabaseDirectory,
                DatabaseFileName = DatabaseFileName,
            };
        }

        /// <summary>
        /// Validate the configuration object
        /// </summary>
        /// <param name="silent">If <c>true</c> the method won't throw exception if the object is not valid.</param>
        /// <param name="validation">If null the configuration is valid, otherwise contain a text indicating the error(s)</param>
        /// <returns><c>true</c> if the configuration is valid, <c>false</c> otherwise.</returns>
        public bool Validate(bool silent, out string validation) => ToDatabaseConfiguration().Validate(silent, out validation);

        public void Save(string filePathName)
        {
            var yaml = ToYamlString(false);
            File.AppendAllText(filePathName, yaml);
        }

        public string ToYamlString(bool omitDefault)
        {
            var serializer = new SerializerBuilder()
                .WithTypeInspector(inner => new CommentGatheringTypeInspector(inner))
                .WithEmissionPhaseObjectGraphVisitor(args => new CommentsObjectGraphVisitor(args.InnerVisitor))
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .ConfigureDefaultValuesHandling(omitDefault ? DefaultValuesHandling.OmitDefaults : DefaultValuesHandling.Preserve)
                .Build();
            CommentsObjectGraphVisitor.OmitDefault = omitDefault;   // Dirty hack...
            return serializer.Serialize(this);
        }

        public static YamlConfiguration FromYamlString(string yaml)
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();
            return deserializer.Deserialize<YamlConfiguration>(yaml);
        }
    }

    #region Serialize YAML Comments
    // Ripped from https://dotnetfiddle.net/8M6iIE
    // Enable to save YAML Comments using the Description attribute
    [ExcludeDocFx]
    public class CommentGatheringTypeInspector : TypeInspectorSkeleton
    {
        private readonly ITypeInspector innerTypeDescriptor;

        public CommentGatheringTypeInspector(ITypeInspector innerTypeDescriptor)
        {
            if (innerTypeDescriptor == null)
            {
                throw new ArgumentNullException("innerTypeDescriptor");
            }

            this.innerTypeDescriptor = innerTypeDescriptor;
        }

        public override IEnumerable<IPropertyDescriptor> GetProperties(Type type, object container)
        {
            return innerTypeDescriptor
                .GetProperties(type, container)
                .Select(d => new CommentsPropertyDescriptor(d));
        }

        private sealed class CommentsPropertyDescriptor : IPropertyDescriptor
        {
            private readonly IPropertyDescriptor baseDescriptor;

            public CommentsPropertyDescriptor(IPropertyDescriptor baseDescriptor)
            {
                this.baseDescriptor = baseDescriptor;
                Name = baseDescriptor.Name;
            }

            public string Name { get; set; }

            public Type Type { get { return baseDescriptor.Type; } }

            public Type TypeOverride
            {
                get { return baseDescriptor.TypeOverride; }
                set { baseDescriptor.TypeOverride = value; }
            }

            public int Order { get; set; }

            public ScalarStyle ScalarStyle
            {
                get { return baseDescriptor.ScalarStyle; }
                set { baseDescriptor.ScalarStyle = value; }
            }

            public bool CanWrite { get { return baseDescriptor.CanWrite; } }

            public void Write(object target, object value)
            {
                baseDescriptor.Write(target, value);
            }

            public T GetCustomAttribute<T>() where T : Attribute
            {
                return baseDescriptor.GetCustomAttribute<T>();
            }

            public IObjectDescriptor Read(object target)
            {
                var description = baseDescriptor.GetCustomAttribute<DescriptionAttribute>();
                return description != null
                    ? new CommentsObjectDescriptor(baseDescriptor.Read(target), description.Description)
                    : baseDescriptor.Read(target);
            }
        }
    }

    [ExcludeDocFx]
    public sealed class CommentsObjectDescriptor : IObjectDescriptor
    {
        private readonly IObjectDescriptor innerDescriptor;

        public CommentsObjectDescriptor(IObjectDescriptor innerDescriptor, string comment)
        {
            this.innerDescriptor = innerDescriptor;
            this.Comment = comment;
        }

        public string Comment { get; private set; }

        public object Value { get { return innerDescriptor.Value; } }
        public Type Type { get { return innerDescriptor.Type; } }
        public Type StaticType { get { return innerDescriptor.StaticType; } }
        public ScalarStyle ScalarStyle { get { return innerDescriptor.ScalarStyle; } }
    }

    [ExcludeDocFx]
    public class CommentsObjectGraphVisitor : ChainedObjectGraphVisitor
    {
        internal static bool OmitDefault;

        public CommentsObjectGraphVisitor(IObjectGraphVisitor<IEmitter> nextVisitor)
            : base(nextVisitor)
        {
        }

        private static object GetDefault(Type type)
        {
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        public override bool EnterMapping(IPropertyDescriptor key, IObjectDescriptor value, IEmitter context)
        {
            var commentsDescriptor = value as CommentsObjectDescriptor;
            if (commentsDescriptor != null && commentsDescriptor.Comment != null)
            {
                // Check if the YAML property being serialized will be omitted and don't push the comment if it's the case
                if (OmitDefault==false || Equals(value.Value, key.GetCustomAttribute<DefaultValueAttribute>()?.Value ?? GetDefault(key.Type)) == false)
                {
                    context.Emit(new Comment(commentsDescriptor.Comment, false));
                }
            }

            return base.EnterMapping(key, value, context);
        }
    }
    #endregion
}
