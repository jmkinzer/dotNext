using System;
using System.Diagnostics.Tracing;

namespace DotNext.Net.Cluster.Consensus.Raft
{
    internal interface ILeaderStateMetrics
    {
        void ReportBroadcastTime(TimeSpan value);
    }

    internal interface IFollowerStateMetrics
    {
        void ReportHeartbeat();
    }

    /// <summary>
    /// Contains a set of callbacks that can be used to report
    /// runtime metrics generated by Raft cluster node.
    /// </summary>
    public class MetricsCollector : ILeaderStateMetrics, IFollowerStateMetrics
    {
        /// <summary>
        /// Reports about broadcast time.
        /// </summary>
        /// <remarks>
        /// Broadcast time is the time spent accessing the cluster nodes caused by Leader states.
        /// </remarks>
        /// <param name="value">The broadcast time.</param>
        public virtual void ReportBroadcastTime(TimeSpan value)
        {
        }

        /// <summary>
        /// Sets counter that allows to track the broadcast time.
        /// </summary>
        public EventCounter? BroadcastTimeCounter
        {
            private get;
#if NETSTANDARD2_1
            set;
#else
            init;
#endif
        }

        void ILeaderStateMetrics.ReportBroadcastTime(TimeSpan value)
        {
            BroadcastTimeCounter?.WriteMetric(value.TotalMilliseconds);
            ReportBroadcastTime(value);
        }

        /// <summary>
        /// Reports that node becomes a candidate.
        /// </summary>
        public virtual void MovedToCandidateState()
            => CandidateStateCounter?.Increment();

        /// <summary>
        /// Sets counter that allows to track the number of transitions to candidate state.
        /// </summary>
        public IncrementingEventCounter? CandidateStateCounter
        {
            private get;
#if NETSTANDARD2_1
            set;
#else
            init;
#endif
        }

        /// <summary>
        /// Reports that node becomes a follower.
        /// </summary>
        public virtual void MovedToFollowerState()
            => FollowerStateCounter?.Increment();

        /// <summary>
        /// Sets counter that allows to track the number of transitions to follower state.
        /// </summary>
        public IncrementingEventCounter? FollowerStateCounter
        {
            private get;
#if NETSTANDARD2_1
            set;
#else
            init;
#endif
        }

        /// <summary>
        /// Reports that node becomes a leader.
        /// </summary>
        public virtual void MovedToLeaderState()
            => LeaderStateCounter?.Increment();

        /// <summary>
        /// Sets counter that allows to track the number of transitions to leader state.
        /// </summary>
        public IncrementingEventCounter? LeaderStateCounter
        {
            private get;
#if NETSTANDARD2_1
            set;
#else
            init;
#endif
        }

        /// <summary>
        /// Reports that node receives a heartbeat from leader node.
        /// </summary>
        public virtual void ReportHeartbeat()
            => HeartbeatCounter?.Increment();

        /// <summary>
        /// Sets counter that allows to track the number of received hearbeats from leader state.
        /// </summary>
        public IncrementingEventCounter? HeartbeatCounter
        {
            private get;
#if NETSTANDARD2_1
            set;
#else
            init;
#endif
        }
    }
}