using FootLook.Core.Interfaces;
using FootLook.Core.Models;
using FootLook.Core.Options;
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


        private readonly Channel<CapturedRequest> _channel =
            Channel.CreateBounded<CapturedRequest>(new BoundedChannelOptions(10_000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

        public ShadowQueue(FootLookOptions options)
        {
            //think of Channel<T> as a conveyor belt, it processes CapturedRequests through the belt.
            //then the CreateUnbounded method means the queue can keep accepting items without a fixed limit.
            // Create a bounded channel with a capacity defined in the options. The channel will drop the oldest item when it reaches its capacity.
            _channel = Channel.CreateBounded<CapturedRequest>(new BoundedChannelOptions(options.QueCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
            });
        }

        //put this captured request into the queue.
        public async ValueTask EnqueueAsync(CapturedRequest request)
        {
            await _channel.Writer.WriteAsync(request);
        }

        //keep reading captured requests from the queue as they arrive.
        IAsyncEnumerable<CapturedRequest> IShadowQueue.DequeueAsync(CancellationToken cancellationToken)
        {
           return _channel.Reader.ReadAllAsync(cancellationToken);
        }
    }
}
