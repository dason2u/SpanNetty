﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace DotNetty.Codecs.Http.Tests.WebSockets.Extensions.Compression
{
    using System;
    using System.Text;
    using DotNetty.Buffers;
    using DotNetty.Codecs.Compression;
    using DotNetty.Codecs.Http.WebSockets;
    using DotNetty.Codecs.Http.WebSockets.Extensions;
    using DotNetty.Codecs.Http.WebSockets.Extensions.Compression;
    using DotNetty.Transport.Channels.Embedded;
    using Xunit;

    public sealed class PerMessageDeflateEncoderTest
    {
        readonly Random _random;

        public PerMessageDeflateEncoderTest()
        {
            _random = new Random();
        }

        [Fact]
        public void CompressedFrame()
        {
            var encoderChannel = new EmbeddedChannel(new PerMessageDeflateEncoder(9, 15, false));
            var decoderChannel = new EmbeddedChannel(
                ZlibCodecFactory.NewZlibDecoder(ZlibWrapper.None));

            // initialize
            var payload = new byte[300];
            _random.NextBytes(payload);
            var frame = new BinaryWebSocketFrame(true,
                WebSocketRsv.Rsv3, Unpooled.WrappedBuffer(payload));

            // execute
            Assert.True(encoderChannel.WriteOutbound(frame));
            var compressedFrame = encoderChannel.ReadOutbound<BinaryWebSocketFrame>();

            // test
            Assert.NotNull(compressedFrame);
            Assert.NotNull(compressedFrame.Content);
            Assert.Equal(WebSocketRsv.Rsv1 | WebSocketRsv.Rsv3, compressedFrame.Rsv);

            Assert.True(decoderChannel.WriteInbound(compressedFrame.Content));
            Assert.True(decoderChannel.WriteInbound(DeflateDecoder.FrameTail.Duplicate()));
            var uncompressedPayload = decoderChannel.ReadInbound<IByteBuffer>();
            Assert.Equal(300, uncompressedPayload.ReadableBytes);

            var finalPayload = new byte[300];
            uncompressedPayload.ReadBytes(finalPayload);
            Assert.Equal(payload, finalPayload);
            uncompressedPayload.Release();
        }

        [Fact]
        public void AlreadyCompressedFrame()
        {
            var encoderChannel = new EmbeddedChannel(new PerMessageDeflateEncoder(9, 15, false));

            // initialize
            var payload = new byte[300];
            _random.NextBytes(payload);

            var frame = new BinaryWebSocketFrame(true,
                WebSocketRsv.Rsv3 | WebSocketRsv.Rsv1, Unpooled.WrappedBuffer(payload));

            // execute
            Assert.True(encoderChannel.WriteOutbound(frame));
            var newFrame = encoderChannel.ReadOutbound<BinaryWebSocketFrame>();

            // test
            Assert.NotNull(newFrame);
            Assert.NotNull(newFrame.Content);
            Assert.Equal(WebSocketRsv.Rsv3 | WebSocketRsv.Rsv1, newFrame.Rsv);
            Assert.Equal(300, newFrame.Content.ReadableBytes);

            var finalPayload = new byte[300];
            newFrame.Content.ReadBytes(finalPayload);
            Assert.Equal(payload, finalPayload);
            newFrame.Release();
        }

        [Fact]
        public void FragmentedFrame()
        {
            var encoderChannel = new EmbeddedChannel(new PerMessageDeflateEncoder(9, 15, false, NeverSkipWebSocketExtensionFilter.Instance));
            var decoderChannel = new EmbeddedChannel(
                    ZlibCodecFactory.NewZlibDecoder(ZlibWrapper.None));

            // initialize
            var payload1 = new byte[100];
            _random.NextBytes(payload1);
            var payload2 = new byte[100];
            _random.NextBytes(payload2);
            var payload3 = new byte[100];
            _random.NextBytes(payload3);

            var frame1 = new BinaryWebSocketFrame(false,
                    WebSocketRsv.Rsv3, Unpooled.WrappedBuffer(payload1));
            var frame2 = new ContinuationWebSocketFrame(false,
                    WebSocketRsv.Rsv3, Unpooled.WrappedBuffer(payload2));
            var frame3 = new ContinuationWebSocketFrame(true,
                    WebSocketRsv.Rsv3, Unpooled.WrappedBuffer(payload3));

            // execute
            Assert.True(encoderChannel.WriteOutbound(frame1));
            Assert.True(encoderChannel.WriteOutbound(frame2));
            Assert.True(encoderChannel.WriteOutbound(frame3));
            var compressedFrame1 = encoderChannel.ReadOutbound<BinaryWebSocketFrame>();
            var compressedFrame2 = encoderChannel.ReadOutbound<ContinuationWebSocketFrame>();
            var compressedFrame3 = encoderChannel.ReadOutbound<ContinuationWebSocketFrame>();

            // test
            Assert.NotNull(compressedFrame1);
            Assert.NotNull(compressedFrame2);
            Assert.NotNull(compressedFrame3);
            Assert.Equal(WebSocketRsv.Rsv1 | WebSocketRsv.Rsv3, compressedFrame1.Rsv);
            Assert.Equal(WebSocketRsv.Rsv3, compressedFrame2.Rsv);
            Assert.Equal(WebSocketRsv.Rsv3, compressedFrame3.Rsv);
            Assert.False(compressedFrame1.IsFinalFragment);
            Assert.False(compressedFrame2.IsFinalFragment);
            Assert.True(compressedFrame3.IsFinalFragment);

            Assert.True(decoderChannel.WriteInbound(compressedFrame1.Content));
            var uncompressedPayload1 = decoderChannel.ReadInbound<IByteBuffer>();
            var finalPayload1 = new byte[100];
            uncompressedPayload1.ReadBytes(finalPayload1);
            Assert.Equal(payload1, finalPayload1);
            uncompressedPayload1.Release();

            Assert.True(decoderChannel.WriteInbound(compressedFrame2.Content));
            var uncompressedPayload2 = decoderChannel.ReadInbound<IByteBuffer>();
            var finalPayload2 = new byte[100];
            uncompressedPayload2.ReadBytes(finalPayload2);
            Assert.Equal(payload2, finalPayload2);
            uncompressedPayload2.Release();

            Assert.True(decoderChannel.WriteInbound(compressedFrame3.Content));
            Assert.True(decoderChannel.WriteInbound(DeflateDecoder.FrameTail.Duplicate()));
            var uncompressedPayload3 = decoderChannel.ReadInbound<IByteBuffer>();
            var finalPayload3 = new byte[100];
            uncompressedPayload3.ReadBytes(finalPayload3);
            Assert.Equal(payload3, finalPayload3);
            uncompressedPayload3.Release();
        }

        [Fact]
        public void CompressionSkipForBinaryFrame()
        {
            EmbeddedChannel encoderChannel = new EmbeddedChannel(new PerMessageDeflateEncoder(9, 15, false, AlwaysSkipWebSocketExtensionFilter.Instance));
            byte[] payload = new byte[300];
            _random.NextBytes(payload);

            WebSocketFrame binaryFrame = new BinaryWebSocketFrame(Unpooled.WrappedBuffer(payload));

            Assert.True(encoderChannel.WriteOutbound(binaryFrame.Copy()));
            var outboundFrame = encoderChannel.ReadOutbound<WebSocketFrame>();

            Assert.Equal(0, outboundFrame.Rsv);
            Assert.Equal(payload, ByteBufferUtil.GetBytes(outboundFrame.Content));
            Assert.True(outboundFrame.Release());

            Assert.False(encoderChannel.Finish());
        }

        [Fact]
        public void SelectivityCompressionSkip()
        {
            var selectivityCompressionFilter = new SelectivityDecompressionFilter0();
            EmbeddedChannel encoderChannel = new EmbeddedChannel(
                    new PerMessageDeflateEncoder(9, 15, false, selectivityCompressionFilter));
            EmbeddedChannel decoderChannel = new EmbeddedChannel(
                    ZlibCodecFactory.NewZlibDecoder(ZlibWrapper.None));

            string textPayload = "not compressed payload";
            byte[] binaryPayload = new byte[101];
            _random.NextBytes(binaryPayload);

            WebSocketFrame textFrame = new TextWebSocketFrame(textPayload);
            BinaryWebSocketFrame binaryFrame = new BinaryWebSocketFrame(Unpooled.WrappedBuffer(binaryPayload));

            Assert.True(encoderChannel.WriteOutbound(textFrame));
            Assert.True(encoderChannel.WriteOutbound(binaryFrame));

            var outboundTextFrame = encoderChannel.ReadOutbound<WebSocketFrame>();

            //compression skipped for textFrame
            Assert.Equal(0, outboundTextFrame.Rsv);
            Assert.Equal(textPayload, outboundTextFrame.Content.ToString(Encoding.UTF8));
            Assert.True(outboundTextFrame.Release());

            var outboundBinaryFrame = encoderChannel.ReadOutbound<WebSocketFrame>();

            //compression not skipped for binaryFrame
            Assert.Equal(WebSocketRsv.Rsv1, outboundBinaryFrame.Rsv);

            Assert.True(decoderChannel.WriteInbound(outboundBinaryFrame.Content.Retain()));
            var uncompressedBinaryPayload = decoderChannel.ReadInbound<IByteBuffer>();

            Assert.Equal(binaryPayload, ByteBufferUtil.GetBytes(uncompressedBinaryPayload));

            Assert.True(outboundBinaryFrame.Release());
            Assert.True(uncompressedBinaryPayload.Release());

            Assert.False(encoderChannel.Finish());
            Assert.False(decoderChannel.Finish());
        }

        [Fact]
        public void IllegalStateWhenCompressionInProgress()
        {
            var selectivityCompressionFilter = new SelectivityDecompressionFilter1();
            EmbeddedChannel encoderChannel = new EmbeddedChannel(
                    new PerMessageDeflateEncoder(9, 15, false, selectivityCompressionFilter));

            byte[] firstPayload = new byte[200];
            _random.NextBytes(firstPayload);

            byte[] finalPayload = new byte[90];
            _random.NextBytes(finalPayload);

            BinaryWebSocketFrame firstPart = new BinaryWebSocketFrame(false, 0, Unpooled.WrappedBuffer(firstPayload));
            ContinuationWebSocketFrame finalPart = new ContinuationWebSocketFrame(true, 0, Unpooled.WrappedBuffer(finalPayload));
            Assert.True(encoderChannel.WriteOutbound(firstPart));

            var outboundFirstPart = encoderChannel.ReadOutbound<BinaryWebSocketFrame>();
            //first part is compressed
            Assert.Equal(WebSocketRsv.Rsv1, outboundFirstPart.Rsv);
            Assert.NotEqual(firstPayload, ByteBufferUtil.GetBytes(outboundFirstPart.Content));
            Assert.True(outboundFirstPart.Release());

            //final part throwing exception
            try
            {
                encoderChannel.WriteOutbound(finalPart);
                Assert.False(true);
            }
            catch (Exception exc)
            {
                var ae = exc as AggregateException;
                Assert.NotNull(ae);
                Assert.IsType<EncoderException>(ae.InnerException);
            }
            finally
            {
                Assert.True(finalPart.Release());
                Assert.False(encoderChannel.FinishAndReleaseAll());
            }
        }

        [Fact]
        public void EmptyFrameCompression()
        {
            EmbeddedChannel encoderChannel = new EmbeddedChannel(new PerMessageDeflateEncoder(9, 15, false));

            TextWebSocketFrame emptyFrame = new TextWebSocketFrame("");

            Assert.True(encoderChannel.WriteOutbound(emptyFrame));
            var emptyDeflateFrame = encoderChannel.ReadOutbound<TextWebSocketFrame>();

            Assert.Equal(WebSocketRsv.Rsv1, emptyDeflateFrame.Rsv);
            Assert.True(ByteBufferUtil.Equals(DeflateDecoder.EmptyDeflateBlock, emptyDeflateFrame.Content));
            // Unreleasable buffer
            Assert.False(emptyDeflateFrame.Release());

            Assert.False(encoderChannel.Finish());
        }

        [Fact]
        public void CodecExceptionForNotFinEmptyFrame()
        {
            EmbeddedChannel encoderChannel = new EmbeddedChannel(new PerMessageDeflateEncoder(9, 15, false));

            TextWebSocketFrame emptyNotFinFrame = new TextWebSocketFrame(false, 0, "");

            try
            {
                encoderChannel.WriteOutbound(emptyNotFinFrame);
                Assert.False(true);
            }
            catch (Exception exc)
            {
                if (exc is AggregateException aggregateException)
                {
                    Assert.IsType<EncoderException>(aggregateException.InnerException);
                }
                else
                {
                    Assert.IsType<EncoderException>(exc);
                }
            }
            finally
            {
                // EmptyByteBuf buffer
                Assert.False(emptyNotFinFrame.Release());
                Assert.False(encoderChannel.Finish());
            }
        }

        sealed class SelectivityDecompressionFilter0 : IWebSocketExtensionFilter
        {
            public bool MustSkip(WebSocketFrame frame)
            {
                return (frame is TextWebSocketFrame || frame is BinaryWebSocketFrame) && frame.Content.ReadableBytes < 100;
            }
        }

        sealed class SelectivityDecompressionFilter1 : IWebSocketExtensionFilter
        {
            public bool MustSkip(WebSocketFrame frame)
            {
                return frame.Content.ReadableBytes < 100;
            }
        }
    }
}
