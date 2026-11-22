package net.oxog.phonebox.deskpanel

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ScreenMirrorTouchPacketTest {

    private fun readU16Le(b: ByteArray, o: Int): Int =
        (b[o].toInt() and 0xFF) or ((b[o + 1].toInt() and 0xFF) shl 8)

    @Test
    fun encodeTouchFrameWritesBinaryHeaderCoordinatesAndPressure() {
        val pkt = ScreenTouchCodec.encodeTouchFrame(
            ids = intArrayOf(3, 7),
            states = intArrayOf(ScreenTouchCodec.STATE_DOWN, ScreenTouchCodec.STATE_MOVE),
            xs = floatArrayOf(0f, 1600f),
            ys = floatArrayOf(360f, 719f),
            pressures = floatArrayOf(0.5f, 2.0f),
            count = 2,
            viewW = 1600,
            viewH = 720,
        )

        assertEquals(18, pkt.size)
        assertEquals(ScreenTouchCodec.OPCODE_TOUCH, pkt[0])
        assertEquals(2, pkt[1].toInt() and 0xFF)

        assertEquals(3, pkt[2].toInt() and 0xFF)
        assertEquals(ScreenTouchCodec.STATE_DOWN, pkt[3].toInt() and 0xFF)
        assertEquals(0, readU16Le(pkt, 4))
        assertEquals(32813, readU16Le(pkt, 6))
        assertEquals(512, readU16Le(pkt, 8))

        assertEquals(7, pkt[10].toInt() and 0xFF)
        assertEquals(ScreenTouchCodec.STATE_MOVE, pkt[11].toInt() and 0xFF)
        assertEquals(65535, readU16Le(pkt, 12))
        assertEquals(65535, readU16Le(pkt, 14))
        assertEquals(1024, readU16Le(pkt, 16))
    }

    @Test
    fun encodeCancelAllProducesZeroContactPacket() {
        val pkt = ScreenTouchCodec.encodeCancelAll()
        assertEquals(2, pkt.size)
        assertEquals(ScreenTouchCodec.OPCODE_TOUCH, pkt[0])
        assertEquals(0, pkt[1].toInt())
        assertFalse(ScreenTouchCodec.isMoveOnly(pkt))
    }

    @Test
    fun touchPacketQueueCoalescesTrailingMoveWithoutDroppingDownOrUp() {
        val q = TouchPacketQueue(maxSize = 4)
        val down = ScreenTouchCodec.encodeTouchFrame(
            intArrayOf(0), intArrayOf(ScreenTouchCodec.STATE_DOWN),
            floatArrayOf(10f), floatArrayOf(10f), floatArrayOf(0.5f), 1, 100, 100,
        )
        val move1 = ScreenTouchCodec.encodeTouchFrame(
            intArrayOf(0), intArrayOf(ScreenTouchCodec.STATE_MOVE),
            floatArrayOf(20f), floatArrayOf(20f), floatArrayOf(0.5f), 1, 100, 100,
        )
        val move2 = ScreenTouchCodec.encodeTouchFrame(
            intArrayOf(0), intArrayOf(ScreenTouchCodec.STATE_MOVE),
            floatArrayOf(50f), floatArrayOf(50f), floatArrayOf(0.5f), 1, 100, 100,
        )
        val up = ScreenTouchCodec.encodeTouchFrame(
            intArrayOf(0), intArrayOf(ScreenTouchCodec.STATE_UP),
            floatArrayOf(50f), floatArrayOf(50f), floatArrayOf(0.5f), 1, 100, 100,
        )

        assertTrue(ScreenTouchCodec.isMoveOnly(move1))
        assertFalse(ScreenTouchCodec.isMoveOnly(down))
        assertFalse(ScreenTouchCodec.isMoveOnly(up))

        q.offer(down)
        q.offer(move1)
        q.offer(move2)
        q.offer(up)

        val p1 = q.take(10)
        val p2 = q.take(10)
        val p3 = q.take(10)
        assertNotNull(p1)
        assertNotNull(p2)
        assertNotNull(p3)
        assertEquals(ScreenTouchCodec.STATE_DOWN, p1!![3].toInt() and 0xFF)
        assertEquals(ScreenTouchCodec.STATE_MOVE, p2!![3].toInt() and 0xFF)
        assertEquals(readU16Le(move2, 4), readU16Le(p2, 4))
        assertEquals(ScreenTouchCodec.STATE_UP, p3!![3].toInt() and 0xFF)
    }
}
