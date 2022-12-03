using NUnit.Framework;
using Coflnet.Sky.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Coflnet.Sky.Sniper.Models;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Sky.Sniper.Services;
public class InternalDataLoaderTest
{
    [Test]
    public void ComparesToOldest()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>()).Build();
        var loader = new InternalDataLoader(null, config, null, null);
        var references = new ConcurrentQueue<ReferencePrice>();
        references.Enqueue(new ReferencePrice() { Day = SniperService.GetDay(DateTime.Now - TimeSpan.FromDays(5)), Price = 1000, Seller = 1, AuctionId = 1 });
        Assert.IsFalse(loader.IsAuctionOlder(new SaveAuction() { End = System.DateTime.Now - TimeSpan.FromDays(10) }, references));
        Assert.IsTrue(loader.IsAuctionOlder(new SaveAuction() { End = System.DateTime.Now - TimeSpan.FromDays(1) }, references));
    }
}
