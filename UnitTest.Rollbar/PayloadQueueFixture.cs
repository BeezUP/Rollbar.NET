﻿#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

namespace UnitTest.Rollbar
{
    using global::Rollbar;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using System.Threading;

    [TestClass]
    [TestCategory(nameof(PayloadQueueFixture))]
    public class PayloadQueueFixture
    {
        [TestInitialize]
        public void SetupFixture()
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        }

        [TestCleanup]
        public void TearDownFixture()
        {

        }

        [TestMethod]
        public void ConstructionTest()
        {
            using (var logger = new RollbarLogger(false))
            {
                RollbarClient client = new RollbarClient(logger);
                PayloadQueue pq = new PayloadQueue(logger, client);
                Assert.IsNotNull(pq.Logger);
                Assert.AreSame(logger, pq.Logger);
                Assert.AreSame(client, pq.Client);
                Assert.AreSame(client.Config, pq.Client.Config);
                Assert.AreSame(client.Config, logger.Config);
            }
        }
    }
}
