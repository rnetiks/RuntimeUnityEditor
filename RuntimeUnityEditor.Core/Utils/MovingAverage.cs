using System;
using System.Collections.Generic;

namespace RuntimeUnityEditor.Core.Utils
{
    internal class MovingAverage
    {
        private readonly long[] _samples;
        private readonly int _windowSize;
        private int _index;
        private int _count;
        private long _sampleAccumulator;

        public MovingAverage(int windowSize = 11)
        {
            _windowSize = windowSize;
            _samples = new long[windowSize];
        }

        public long GetAverage()
        {
            if (_count == 0)
                return 0;

            return _sampleAccumulator / _count;
        }

        public void Sample(long newSample)
        {
            if (_count >= _windowSize)
                _sampleAccumulator -= _samples[_index];
            else
                _count++;

            _sampleAccumulator += newSample;
            _samples[_index] = newSample;
            _index = (_index + 1) % _windowSize;
        }

        public void Reset()
        {
            Array.Clear(_samples, 0, _samples.Length);
            _index = 0;
            _count = 0;
            _sampleAccumulator = 0;
        }
    }
}