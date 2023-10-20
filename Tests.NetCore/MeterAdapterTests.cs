﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using SDM=System.Diagnostics.Metrics;

namespace Prometheus.Tests
{
    [TestClass]
    public class MeterAdapterTests: IDisposable
    {
        private CollectorRegistry _registry;
        private MetricFactory _metrics;
        private readonly SDM.Meter _meter = new("test");
        private readonly SDM.Counter<long> _intCounter;
        private readonly SDM.Counter<double> _floatCounter;
        private readonly SDM.Histogram<double> _histogram;
        private IDisposable _adapter;

        public MeterAdapterTests()
        {
            _registry = Metrics.NewCustomRegistry();
            _metrics = Metrics.WithCustomRegistry(_registry);

            _intCounter = _meter.CreateCounter<long>("int_counter");
            _floatCounter = _meter.CreateCounter<double>("float_counter");
            _histogram = _meter.CreateHistogram<double>("histogram");

            _registry = Metrics.NewCustomRegistry();
            _metrics = Metrics.WithCustomRegistry(_registry);

            _adapter = MeterAdapter.StartListening(new MeterAdapterOptions {
                InstrumentFilterPredicate = instrument => {
                    return instrument.Meter == _meter;
                },
                Registry = _registry,
                MetricFactory = _metrics,
                ResolveHistogramBuckets = instrument => new double[] { 1, 2, 3, 4 },
            });
        }

        private FakeSerializer SerializeMetrics()
        {
            var serializer = new FakeSerializer();
            _registry.CollectAndSerializeAsync(serializer, default).Wait();
            return serializer;
        }

        private double GetValue(string meterName, params (string name, string value)[] labels)
        {
            var serializer = SerializeMetrics();
            if (serializer.Data.Count == 0)
                throw new Exception("No metrics found");
            var labelsString = string.Join(",", labels.Select(l => $"{l.name}=\"{l.value}\""));
            foreach (var d in serializer.Data)
            {
                Console.WriteLine($"{d.name} {d.labels} {d.canonicalLabel} {d.value}");

                if (d.name == meterName && d.labels == labelsString)
                {
                    return d.value;
                }
            }
            if (serializer.Data.Any(d => d.name == meterName))
                throw new Exception($"Metric {meterName}{{{labelsString}}} not found, only these labels were found: {string.Join(" / ", serializer.Data.Where(d => d.name == meterName).Select(d => d.labels))}");
            throw new Exception($"Metric {meterName} not found, only these metrics were found: {string.Join(" / ", serializer.Data.Select(d => d.name).Distinct())}");
        }

        [TestMethod]
        public void CounterInt()
        {
            _intCounter.Add(1);
            Assert.AreEqual(1, GetValue("test_int_counter"));
            _intCounter.Add(2);
            Assert.AreEqual(3, GetValue("test_int_counter"));
        }

        [TestMethod]
        public void CounterFloat()
        {
            _floatCounter.Add(1);
            Assert.AreEqual(1, GetValue("test_float_counter"));
            _floatCounter.Add(0.002);
            Assert.AreEqual(1.002, GetValue("test_float_counter"));
        }

        [TestMethod]
        public void CounterLabels()
        {
            _intCounter.Add(1, new ("l1", "value"), new ("l2", 111));
            Assert.AreEqual(1, GetValue("test_int_counter", ("l1", "value"), ("l2", "111")));
            _intCounter.Add(1000);
            _intCounter.Add(1000, new ("l1", "value"), new ("l2", 0));
            _intCounter.Add(1000, new KeyValuePair<string, object?>("l1", "value"));
            _intCounter.Add(1, new ("l2", 111), new ("l1", "value"));
            Assert.AreEqual(2, GetValue("test_int_counter", ("l1", "value"), ("l2", "111")));
            Assert.AreEqual(1000, GetValue("test_int_counter", ("l1", "value"), ("l2", "0")));
            Assert.AreEqual(1000, GetValue("test_int_counter", ("l1", "value")));
            Assert.AreEqual(1000, GetValue("test_int_counter"));
        }

        [TestMethod]
        public void LabelRenaming()
        {
            _intCounter.Add(1, new ("my-label", 1), new ("Another.Label", 1));
            Assert.AreEqual(1, GetValue("test_int_counter", ("another_label", "1"), ("my_label", "1")));
        }


        public void Dispose()
        {
            _adapter.Dispose();
        }

        class FakeSerializer : IMetricsSerializer
        {
            public List<(string name, string labels, string canonicalLabel, double value, ObservedExemplar exemplar)> Data = new();
            public Task FlushAsync(CancellationToken cancel) => Task.CompletedTask;
            public Task WriteEnd(CancellationToken cancel) => Task.CompletedTask;

            public Task WriteFamilyDeclarationAsync(string name, byte[] nameBytes, byte[] helpBytes, MetricType type, byte[] typeBytes, CancellationToken cancel) => Task.CompletedTask;

            public Task WriteMetricPointAsync(byte[] name, byte[] flattenedLabels, CanonicalLabel canonicalLabel, CancellationToken cancel, double value, ObservedExemplar exemplar, byte[] suffix = null)
            {
                Data.Add((
                    name: Encoding.UTF8.GetString(name),
                    labels: Encoding.UTF8.GetString(flattenedLabels),
                    canonicalLabel: canonicalLabel.ToString(),
                    value: value,
                    exemplar: exemplar
                ));
                return Task.CompletedTask;
            }

            public Task WriteMetricPointAsync(byte[] name, byte[] flattenedLabels, CanonicalLabel canonicalLabel, CancellationToken cancel, long value, ObservedExemplar exemplar, byte[] suffix = null) =>
                WriteMetricPointAsync(name, flattenedLabels, canonicalLabel, cancel, (double)value, exemplar, suffix);
        }
    }
}
