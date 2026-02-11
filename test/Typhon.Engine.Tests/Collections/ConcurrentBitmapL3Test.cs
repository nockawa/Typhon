using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace Typhon.Engine.Tests;

public class ConcurrentBitmapL3Test
{
    [Test]
    public void EnumerationTest()
    {
        var bitCount = 64 * 64 * 64 * 2;
        var c = new ConcurrentBitmapL3Any(bitCount);

        var values = new List<int>(new []{0, 2, 64, 64*64*2});
        foreach (var v in values)
        {
            c.Set(v);
        }

        var i = 0;
        foreach (var bitIndex in c)
        {
            Assert.That(bitIndex, Is.EqualTo(values[i++]));
        }
    }

}