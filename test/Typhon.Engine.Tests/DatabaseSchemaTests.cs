using NUnit.Framework;

namespace Typhon.Engine.Tests
{
    public class DatabaseSchemaTests
    {
        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public void TestDatabaseSchema()
        {
            var dc = new DatabaseDefinitions();
            dc.CreateComponentBuilder("DBObject")
                .WithField("DBObjectTypeName", FieldType.String64).IsStatic()
                .WithField("ID", FieldType.Long)
                .Build();
        }
    }
}