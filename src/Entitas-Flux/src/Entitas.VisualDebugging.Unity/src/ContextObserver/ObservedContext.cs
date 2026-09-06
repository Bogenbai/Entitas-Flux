using System.Collections.Generic;
using System.Text;

namespace Entitas.VisualDebugging.Unity
{
    public class ObservedContext
    {
        public IContext context => _context;
        public IReadOnlyList<IGroup> groups => _groups;

        readonly IContext _context;
        readonly List<IGroup> _groups = new List<IGroup>();
        readonly StringBuilder _toStringBuilder = new StringBuilder();

        public ObservedContext(IContext context)
        {
            _context = context;
            _context.OnGroupCreated += onGroupCreated;
        }

        public void Deactivate() => _context.OnGroupCreated -= onGroupCreated;

        void onGroupCreated(IContext context, IGroup group) => _groups.Add(group);

        public override string ToString()
        {
            _toStringBuilder.Length = 0;
            _toStringBuilder
                .Append(_context.contextInfo.name).Append(" (")
                .Append(_context.count).Append(" entities, ")
                .Append(_context.reusableEntitiesCount).Append(" reusable, ");

            if (_context.retainedEntitiesCount != 0)
            {
                _toStringBuilder
                    .Append(_context.retainedEntitiesCount).Append(" retained, ");
            }

            _toStringBuilder
                .Append(_groups.Count)
                .Append(" groups)");

            return _toStringBuilder.ToString();
        }
    }
}
