package net.oxog.phonebox.deskpanel

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ScreenMirrorAudioTest {

    @Test
    fun parseMetaParsesActiveAndInactiveHeaders() {
        val activeMeta = byteArrayOf(
            0x04,
            0x00,
            0x80.toByte(), 0xBB.toByte(), 0x00, 0x00, // 48000 LE
            0x02,
            0xC0.toByte(), 0x03, // 960 LE
            0x03, // active + virtual
        )
        val parsed = ScreenAudioWire.parseMeta(activeMeta, activeMeta.size)
        assertNotNull(parsed)
        assertEquals(0, parsed!!.codec)
        assertEquals(48_000, parsed.sampleRate)
        assertEquals(2, parsed.channels)
        assertEquals(960, parsed.frameSamples)
        assertTrue(parsed.active)
        assertTrue(parsed.isVirtual)

        val offMeta = byteArrayOf(
            0x04,
            0xFF.toByte(),
            0x80.toByte(), 0xBB.toByte(), 0x00, 0x00,
            0x02,
            0xC0.toByte(), 0x03,
            0x00,
        )
        val parsedOff = ScreenAudioWire.parseMeta(offMeta, offMeta.size)
        assertNotNull(parsedOff)
        assertEquals(false, parsedOff!!.active)

        assertNull(ScreenAudioWire.parseMeta(byteArrayOf(0x04, 0x00), 2))
        assertNull(ScreenAudioWire.parseMeta(byteArrayOf(0x02, 0x00, 0, 0, 0, 0, 2, 0, 1, 1), 10))
    }

    @Test
    fun parseFramePayloadLengthValidatesHeaderAndBounds() {
        val valid = ByteArray(15 + 3840)
        valid[0] = 0x03
        valid[1] = 0x00
        valid[2] = 0x0F // 3840 LE
        assertEquals(3840, ScreenAudioWire.parseFramePayloadLength(valid, valid.size))

        // Truncated buffer
        assertEquals(-1, ScreenAudioWire.parseFramePayloadLength(valid, 100))
        // Wrong opcode
        val wrongOp = valid.copyOf().apply { this[0] = 0x01 }
        assertEquals(-1, ScreenAudioWire.parseFramePayloadLength(wrongOp, wrongOp.size))
        // Zero payload length
        val zeroLen = ByteArray(15).apply { this[0] = 0x03 }
        assertEquals(-1, ScreenAudioWire.parseFramePayloadLength(zeroLen, zeroLen.size))
    }

    @Test
    fun audioPacketQueueDropsOldestAtCapacity() {
        val q = AudioPacketQueue(maxPackets = 3)
        q.offer(byteArrayOf(1))
        q.offer(byteArrayOf(2))
        q.offer(byteArrayOf(3))
        q.offer(byteArrayOf(4))

        assertEquals(3, q.size())
        assertArrayEquals(byteArrayOf(2), q.take(10))
        assertArrayEquals(byteArrayOf(3), q.take(10))
        assertArrayEquals(byteArrayOf(4), q.take(10))
        assertNull(q.take(5))
    }

    @Test
    fun applyFadeInPcm16LeRampsStartAndPreservesTail() {
        val pcm = ByteArray(16) // 4 stereo frames
        for (i in 0 until 8) {
            pcm[i * 2] = 0x10
            pcm[i * 2 + 1] = 0x27 // 10000 LE
        }
        ScreenAudioWire.applyFadeInPcm16Le(pcm, 0, pcm.size, channels = 2, fadeFrames = 2)

        fun readSample(idx: Int): Int {
            val p = idx * 2
            return ((pcm[p].toInt() and 0xFF) or (pcm[p + 1].toInt() shl 8)).toShort().toInt()
        }

        assertEquals(0, readSample(0))
        assertEquals(0, readSample(1))
        assertEquals(5000, readSample(2))
        assertEquals(5000, readSample(3))
        assertEquals(10000, readSample(4))
        assertEquals(10000, readSample(6))
    }
}
