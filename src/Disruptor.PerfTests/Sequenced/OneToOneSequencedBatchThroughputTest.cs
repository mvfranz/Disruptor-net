﻿using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Disruptor.Dsl;
using Disruptor.PerfTests.Support;

namespace Disruptor.PerfTests.Sequenced
{
    /// <summary>
    /// UniCast a series of items between 1 publisher and 1 event processor
    ///
    /// <code>
    /// +----+    +-----+
    /// | P1 |--->| EP1 |
    /// +----+    +-----+
    ///
    /// Disruptor:
    /// ==========
    ///              track to prevent wrap
    ///              +------------------+
    ///              |                  |
    ///              |                  v
    /// +----+    +====+    +====+   +-----+
    /// | P1 |---›| RB |‹---| SB |   | EP1 |
    /// +----+    +====+    +====+   +-----+
    ///      claim      get    ^        |
    ///                        |        |
    ///                        +--------+
    ///                          waitFor
    ///
    /// P1  - Publisher 1
    /// RB  - RingBuffer
    /// SB  - SequenceBarrier
    /// EP1 - EventProcessor 1
    /// </code>
    /// </summary>
    public class OneToOneSequencedBatchThroughputTest : IThroughputTest
    {
        private const int _batchSize = 10;
        private const int _bufferSize = 1024 * 64;
        private const long _iterations = 1000L * 1000L * 100L;
        private static readonly long _expectedResult = PerfTestUtil.AccumulatedAddition(_iterations) * _batchSize;

        ///////////////////////////////////////////////////////////////////////////////////////////////

        private readonly RingBuffer<PerfEvent> _ringBuffer;
        private readonly AdditionEventHandler _handler;
        private readonly IBatchEventProcessor<PerfEvent> _batchEventProcessor;

        public OneToOneSequencedBatchThroughputTest()
        {
            _ringBuffer = RingBuffer<PerfEvent>.CreateSingleProducer(PerfEvent.EventFactory, _bufferSize, new YieldingWaitStrategy());
            var sequenceBarrier = _ringBuffer.NewBarrier();
            _handler = new AdditionEventHandler();
            _batchEventProcessor = BatchEventProcessorFactory.Create(_ringBuffer, sequenceBarrier, _handler);
            _ringBuffer.AddGatingSequences(_batchEventProcessor.Sequence);
        }

        ///////////////////////////////////////////////////////////////////////////////////////////////

        public int RequiredProcessorCount => 2;

        public long Run(ThroughputSessionContext sessionContext)
        {
            var expectedCount = _batchEventProcessor.Sequence.Value + _iterations * _batchSize;
            _handler.Reset(expectedCount);
            var processorTask = _batchEventProcessor.Start();
            _batchEventProcessor.WaitUntilStarted(TimeSpan.FromSeconds(5));

            sessionContext.Start();

            var ringBuffer = _ringBuffer;
            for (var i = 0; i < _iterations; i++)
            {
                var hi = ringBuffer.Next(_batchSize);
                var lo = hi - (_batchSize - 1);
                for (var l = lo; l <= hi; l++)
                {
                    ringBuffer[l].Value = (i);
                }
                ringBuffer.Publish(lo, hi);
            }

            _handler.WaitForSequence();
            sessionContext.Stop();
            PerfTestUtil.WaitForEventProcessorSequence(expectedCount, _batchEventProcessor);
            _batchEventProcessor.Halt();
            processorTask.Wait(2000);

            sessionContext.SetBatchData(_handler.BatchesProcessed, _iterations * _batchSize);

            PerfTestUtil.FailIfNot(_expectedResult, _handler.Value, $"Handler should have processed {_expectedResult} events, but was: {_handler.Value}");

            return _batchSize * _iterations;
        }
    }
}
