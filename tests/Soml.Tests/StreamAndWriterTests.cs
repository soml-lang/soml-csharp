using System.Collections;
using System.Text;
using System.Runtime.InteropServices;
using Soml.Serialization;

namespace Soml.Tests;

public sealed class StreamAndWriterTests
{
	private sealed class AdvertisedLengthStream(byte[] contents) : Stream
	{
		private readonly MemoryStream _contents = new(contents);

		public override bool CanRead => true;
		public override bool CanSeek => true;
		public override bool CanWrite => false;
		public override long Length => 32 * 1024 * 1024;
		public override long Position
		{
			get => _contents.Position;
			set => throw new NotSupportedException();
		}

		public override int Read(byte[] buffer, int offset, int count) => _contents.Read(buffer, offset, count);
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		public override void Flush() => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				_contents.Dispose();
			}

			base.Dispose(disposing);
		}
	}

	[Fact]
	public void StreamLengthDoesNotDetermineAnUnboundedInitialAllocation()
	{
		using (var warmup = new AdvertisedLengthStream("a: 1"u8.ToArray()))
		{
			Assert.Equal(1, SomlValue.Parse(warmup)["a"].GetInt64());
		}

		using var input = new AdvertisedLengthStream("a: 1"u8.ToArray());
		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var value = SomlValue.Parse(input);
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

		Assert.Equal(1, value["a"].GetInt64());
		Assert.True(allocated < 1_000_000, $"Reading a four-byte stream allocated {allocated} bytes");
	}

	private sealed class CountedModel
	{
		public int Reads { get; private set; }
		public int Value
		{
			get
			{
				Reads++;
				throw new InvalidOperationException("The getter failed");
			}
		}
	}

	[Fact]
	public async Task CancellationDoesNotMaskGetterFailuresDuringDocumentValidation()
	{
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		using var output = new MemoryStream();
		var value = new CountedModel();

		var error = await Assert.ThrowsAsync<SomlException>(() => SomlSerializer.SerializeAsync(output, value, cancellationToken: cancellation.Token));

		Assert.IsType<InvalidOperationException>(error.InnerException);
		Assert.Equal("value", error.Path);
		Assert.Equal(1, value.Reads);
		Assert.Equal(0, output.Length);
		Assert.True(output.CanWrite);
	}

	[Fact]
	public void NullTypeInformationIsRejectedBeforeConsumingAStream()
	{
		using var input = new MemoryStream("a: 1"u8.ToArray());

		Assert.Throws<ArgumentNullException>(() => SomlSerializer.Deserialize(input, (SomlTypeInfo<SomlObject>)null!));
		Assert.Equal(0, input.Position);
	}

	[Fact]
	public void NonGenericObjectEnumeratorRejectsCurrentOutsideEnumeration()
	{
		var value = new SomlObject { ["a"] = 1 };
		var enumerator = ((IEnumerable)value).GetEnumerator();

		Assert.Throws<InvalidOperationException>(() => enumerator.Current);
		Assert.True(enumerator.MoveNext());
		Assert.Equal(new KeyValuePair<string, SomlValue>("a", 1), enumerator.Current);
		Assert.False(enumerator.MoveNext());
		Assert.Throws<InvalidOperationException>(() => enumerator.Current);
		enumerator.Reset();
		Assert.Throws<InvalidOperationException>(() => enumerator.Current);
	}

	[Fact]
	public void ResetRejectsAnInvalidatedObjectEnumerator()
	{
		var value = new SomlObject { ["a"] = 1 };
		var enumerator = value.GetEnumerator();
		value.Add("b", 2);

		Assert.Throws<InvalidOperationException>(() => enumerator.Reset());
	}

	[Fact]
	public async Task NullTypeInformationIsRejectedBeforeConsumingAnAsyncStream()
	{
		using var input = new MemoryStream("a: 1"u8.ToArray());

		await Assert.ThrowsAsync<ArgumentNullException>(() => SomlSerializer.DeserializeAsync(input, (SomlTypeInfo<SomlObject>)null!, TestContext.Current.CancellationToken).AsTask());
		Assert.Equal(0, input.Position);
	}

	[Fact]
	public void NullTypeInformationIsRejectedBeforeInvalidUtf8()
	{
		Assert.Throws<ArgumentNullException>(() => SomlSerializer.Deserialize((ReadOnlySpan<byte>)[0xFF], (SomlTypeInfo<SomlObject>)null!));
	}

	[Fact]
	public void NullRuntimeTypeIsRejectedBeforeParsingAnInvalidDocument()
	{
		Assert.Throws<ArgumentNullException>(() => SomlSerializer.Deserialize("a: [", (Type)null!));
	}

	[Fact]
	public async Task CancellationDoesNotMaskGeneratedConverterFactoryFailures()
	{
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		using var output = new MemoryStream();
		var typeInfo = new SomlTypeInfo<SomlObject>(GeneratorContext.Default, () => throw new InvalidOperationException("The converter factory failed"));

		var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SomlSerializer.SerializeAsync(output, new SomlObject(), typeInfo, cancellation.Token));

		Assert.Equal("The converter factory failed", error.Message);
		Assert.Equal(0, output.Length);
	}

	private sealed class TrackedWriteStream : MemoryStream
	{
		public int LargestWrite { get; private set; }
		public byte[]? RentedBuffer { get; private set; }
		public bool FailOnWrite { get; init; }

		public override void Write(ReadOnlySpan<byte> buffer)
		{
			LargestWrite = Math.Max(LargestWrite, buffer.Length);
			base.Write(buffer);
		}

		public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
		{
			LargestWrite = Math.Max(LargestWrite, buffer.Length);
			Assert.True(MemoryMarshal.TryGetArray(buffer, out var segment));
			RentedBuffer = segment.Array;

			if (FailOnWrite)
			{
				throw new IOException("The output stream failed");
			}

			return base.WriteAsync(buffer, cancellationToken);
		}
	}

	[Theory]
	[InlineData(false, -3)]
	[InlineData(false, -2)]
	[InlineData(false, -1)]
	[InlineData(false, 0)]
	[InlineData(false, 1)]
	[InlineData(true, -3)]
	[InlineData(true, -2)]
	[InlineData(true, -1)]
	[InlineData(true, 0)]
	[InlineData(true, 1)]
	public async Task LargeUtf8DocumentsUseBoundedWritesAndPreserveSurrogatePairs(bool asynchronous, int boundaryOffset)
	{
		// The serialized prefix is ASCII, so these offsets place the emoji before, across, or after the actual 16KiB byte boundary.
		var text = new string('a', (16 * 1024) - "value: '".Length + boundaryOffset) + "😀é" + new string('界', 16384) + "😀";
		var value = new SomlObject { ["value"] = text };
		using var output = new TrackedWriteStream();

		if (asynchronous)
		{
			await SomlSerializer.SerializeAsync(output, value, cancellationToken: TestContext.Current.CancellationToken);
		}
		else
		{
			SomlSerializer.Serialize(output, value);
		}

		Assert.Equal(SomlSerializer.SerializeToUtf8Bytes(value), output.ToArray());
		Assert.InRange(output.LargestWrite, 1, 16 * 1024);
		Assert.Equal(text, SomlValue.Parse(output.ToArray())["value"].GetString());
		Assert.True(output.CanWrite);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task PooledUtf8BufferDoesNotRetainDocumentContents(bool failOnWrite)
	{
		using var output = new TrackedWriteStream { FailOnWrite = failOnWrite };
		var write = SomlSerializer.SerializeAsync(output, new SomlObject { ["secret"] = "do not retain me" }, cancellationToken: TestContext.Current.CancellationToken);

		if (failOnWrite)
		{
			await Assert.ThrowsAsync<IOException>(() => write);
		}
		else
		{
			await write;
		}

		Assert.NotNull(output.RentedBuffer);
		Assert.DoesNotContain(output.RentedBuffer, value => value != 0);
	}

	[Fact]
	public void ArrayCopyHasIndependentStorageAndRetainsValueReferences()
	{
		var child = new SomlObject { ["a"] = 1 };
		var source = new SomlArray { child, "original" };
		var copy = new SomlArray(source);
		source[1] = "changed";
		source.Add(3);

		Assert.Equal(2, copy.Count);
		Assert.Equal("original", copy[1].GetString());
		Assert.Same(child, copy[0].GetObject());
	}

	[Fact]
	public void ConcurrentObjectSearchesPublishACompleteIndex()
	{
		var value = new SomlObject();

		for (var index = 0; index < 128; index++)
		{
			value.Add($"key{index}", index);
		}

		Parallel.For(0, 64, iteration =>
		{
			for (var offset = 0; offset < 128; offset++)
			{
				var index = (iteration + offset) % 128;
				Assert.Equal(index, value[$"key{index}"].GetInt64());
			}
		});
	}
}
