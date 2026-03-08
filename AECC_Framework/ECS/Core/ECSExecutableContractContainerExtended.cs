using AECC.Collections;
using AECC.Core;
using AECC.ECS.Core;

namespace AECC.ECS.Core
{
    public class ECSExecutableContractContainerExtended : ECSExecutableContractContainer
    {
        /// <summary>
        /// Need to setup in initalize method. Setting up look like is:
        /// SystemEventHandler.Add(GameEvent.Id, new List<Func<ECSEvent, object>>() {
        ///         (Event) => {
        ///             return (Event as GameEvent);
        ///         }
        ///     });
        /// </summary>
        public IDictionary<long, List<Func<ECSEvent, object>>> SystemEventHandler = new DictionaryWrapper<long, List<Func<ECSEvent, object>>>();
        public ECSExecutableContractContainerExtended() : base() { }

        /// <summary>
        /// Return system events components dictionary <StaticIDEvent, randomint>
        /// </summary>
        /// <returns></returns>
        public virtual IDictionary<long, int> GetInterestedEventsList()
        {
            var result = new DictionaryWrapper<long, int>();
            foreach (var eventid in SystemEventHandler)
            {
                result.TryAdd(eventid.Key, 0);
            }
            return result;
        }
    }
}