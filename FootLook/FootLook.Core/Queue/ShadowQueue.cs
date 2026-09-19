using FootLook.Core.Interfaces;
using FootLook.Core.Models;
using FootLook.Core.Options;
using FootLook.Core.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FootLook.Core.Queue
{
    public class ShadowQueue : IShadowQueue
    {
        private readonly Channel<CapturedRequest> _channel;
        private readonly int _capacity;
        private readonly CaptureReliabilityState _reliabilityState;

        public ShadowQueue(FootLookOptions options, CaptureReliabilityState reliabilityState)
        {
            _reliabilityState = reliabilityState;
            _capacity = Math.Max(1, options.QueCapacity);

            //think of Channel<T> as a conveyor belt, it processes CapturedRequests through the belt.
            // Create a bounded channel with a capacity defined in the options. The channel will drop the oldest item when it reaches its capacity.
            _channel = Channel.CreateBounded<CapturedRequest>(new BoundedChannelOptions(_capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
        }

        //put this captured request into the queue.
        public async ValueTask EnqueueAsync(CapturedRequest request)
        {
            // Channel<T>'s DropOldest mode silently evicts the oldest queued item to make
            // room, with no callback or signal that it happened - this was previously
            // completely invisible anywhere in the system. This is a best-effort detection
            // (a Count snapshot immediately before writing, under concurrent writers so it
            // can race) rather than an exact count, but it turns "silent, unknown data
            // loss" into "approximately this many captures were dropped and here's when",
            // which is what CaptureReliabilityState's event-loss-rate reporting needs.
            var likelyAtCapacity = _channel.Reader.CanCount && _channel.Reader.Count >= _capacity;

            await _channel.Writer.WriteAsync(request);

            if (likelyAtCapacity)
            {
                _reliabilityState.RecordQueueDrop($"queue at capacity ({_capacity}); oldest capture evicted");
            }
        }

        //keep reading captured requests from the queue as they arrive.
        IAsyncEnumerable<CapturedRequest> IShadowQueue.DequeueAsync(CancellationToken cancellationToken)
        {
           return _channel.Reader.ReadAllAsync(cancellationToken);
        }
    }
}
