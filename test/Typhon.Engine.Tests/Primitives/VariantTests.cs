using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests;

class VariantTests
{

    [Test]
    public void StringTest()
    {
        {
            string value = "Pouet";
            var variant = new Variant(value, false);

            var str = (string)variant;
        
            Assert.That(str, Is.EqualTo(value));
        }

        {
            string veryLong = "Home, It's becoming a killing field\nThere's a cross hair locked on my heart";
            string trunLong = "Home, It's becoming a killing field\nThere's a cross hair loc";

            Assert.Throws<InvalidOperationException>(() =>
            {
                var variant = new Variant(veryLong, false);
            });

            var truncated = new Variant(veryLong, true);
            Assert.That((string)truncated, Is.EqualTo(trunLong));
        }
    }

    [Test]
    public void BoolTest()
    {
        {
            var variant = new Variant(true);
            var val = (bool)variant;
            
            Assert.That(val, Is.True);
        }
        {
            var variant = new Variant(false);
            var val = (bool)variant;
            
            Assert.That(val, Is.False);
        }
    }

    [Test]
    public void ByteTest()
    {
        {
            var variant = new Variant((sbyte)123);
            var val = (sbyte)variant;
            
            Assert.That(val, Is.EqualTo(123));
        }
        {
            var variant = new Variant((sbyte)-64);
            var val = (sbyte)variant;
            
            Assert.That(val, Is.EqualTo(-64));
        }
    }

    [Test]
    public void ShortTest()
    {
        {
            var variant = new Variant((short)16123);
            var val = (short)variant;
            
            Assert.That(val, Is.EqualTo(16123));
        }
        {
            var variant = new Variant((short)-10123);
            var val = (short)variant;
            
            Assert.That(val, Is.EqualTo(-10123));
        }
    }
    
    [Test]
    public void IntTest()
    {
        {
            var variant = new Variant((int)16123456);
            var val = (int)variant;
            
            Assert.That(val, Is.EqualTo(16123456));
        }
        {
            var variant = new Variant((int)-10123456);
            var val = (int)variant;
            
            Assert.That(val, Is.EqualTo(-10123456));
        }
    }
    
    [Test]
    public void LongTest()
    {
        {
            var variant = new Variant(16_123_456_789);
            var val = (long)variant;
            
            Assert.That(val, Is.EqualTo(16_123_456_789));
        }
        {
            var variant = new Variant(-10_123_456_789);
            var val = (long)variant;
            
            Assert.That(val, Is.EqualTo(-10_123_456_789));
        }
    }
}