// unset

using System.Collections.Generic;

namespace Typhon.Engine
{
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
}