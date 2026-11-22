package net.oxog.phonebox.deskpanel

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

class ScreenMirrorLifecycleTest {

    @Test
    fun codecGuardStopsAndReleasesOnceWithoutOverlappingActiveAccess() {
        val stopCount = AtomicInteger(0)
        val releaseCount = AtomicInteger(0)
        val insideAccess = AtomicBoolean(false)
        val overlapped = AtomicBoolean(false)

        val guard = CodecGuard(
            resource = "decoder",
            onStop = {
                if (insideAccess.get()) overlapped.set(true)
                stopCount.incrementAndGet()
            },
            onRelease = {
                if (insideAccess.get()) overlapped.set(true)
                releaseCount.incrementAndGet()
            },
        )

        val entered = CountDownLatch(1)
        val releaseCalled = CountDownLatch(1)
        val worker = Thread {
            val result = guard.withAccess("fallback") {
                insideAccess.set(true)
                entered.countDown()
                Thread.sleep(40)
                insideAccess.set(false)
                "ok"
            }
            assertEquals("ok", result)
        }
        worker.start()
        assertTrue(entered.await(2, TimeUnit.SECONDS))

        assertTrue(guard.releaseSafely())
        releaseCalled.countDown()
        worker.join(2000)

        assertTrue(guard.releaseSafely())
        assertFalse(overlapped.get())
        assertEquals(1, stopCount.get())
        assertEquals(1, releaseCount.get())
        assertTrue(guard.isStopping())
        assertEquals("fallback", guard.withAccess("fallback") { "should-not-run" })
    }

    @Test
    fun codecGuardBoundedReleaseTimesOutCleanlyWhenAccessIsHeld() {
        val stopCount = AtomicInteger(0)
        val releaseCount = AtomicInteger(0)
        val guard = CodecGuard(
            resource = "decoder",
            onStop = { stopCount.incrementAndGet() },
            onRelease = { releaseCount.incrementAndGet() },
        )

        val entered = CountDownLatch(1)
        val allowExit = CountDownLatch(1)
        val holder = Thread {
            guard.withAccess(Unit) {
                entered.countDown()
                allowExit.await(2, TimeUnit.SECONDS)
            }
        }
        holder.start()
        assertTrue(entered.await(2, TimeUnit.SECONDS))

        assertFalse(guard.releaseSafely(maxWaitMs = 20L))
        assertTrue(guard.isStopping())
        assertEquals(0, stopCount.get())
        assertEquals(0, releaseCount.get())

        allowExit.countDown()
        holder.join(2000)

        assertTrue(guard.releaseSafely())
        assertEquals(1, stopCount.get())
        assertEquals(1, releaseCount.get())
    }

    @Test
    fun classifyUpgradeStatusRoutesFallbackAndSessionInactiveCorrectly() {
        assertEquals(ScreenMirrorWsClient.UpgradeAction.OK, ScreenMirrorWsClient.classifyUpgradeStatus(101))
        assertEquals(ScreenMirrorWsClient.UpgradeAction.FALLBACK, ScreenMirrorWsClient.classifyUpgradeStatus(404))
        assertEquals(ScreenMirrorWsClient.UpgradeAction.FALLBACK, ScreenMirrorWsClient.classifyUpgradeStatus(405))
        assertEquals(ScreenMirrorWsClient.UpgradeAction.FALLBACK, ScreenMirrorWsClient.classifyUpgradeStatus(501))
        assertEquals(ScreenMirrorWsClient.UpgradeAction.SESSION_INACTIVE, ScreenMirrorWsClient.classifyUpgradeStatus(409))
        assertEquals(ScreenMirrorWsClient.UpgradeAction.ERROR, ScreenMirrorWsClient.classifyUpgradeStatus(500))
        assertEquals(ScreenMirrorWsClient.UpgradeAction.ERROR, ScreenMirrorWsClient.classifyUpgradeStatus(403))
    }

    @Test
    fun inFlightTrackerEnforcesWarmupAndPrimedLimits() {
        val tracker = InFlightTracker(maxInFlight = 2, warmupMax = 4)
        assertTrue(tracker.awaitSlot(isIdr = true, waitMs = 0))
        assertTrue(tracker.awaitSlot(isIdr = false, waitMs = 0))
        assertTrue(tracker.awaitSlot(isIdr = false, waitMs = 0))
        assertTrue(tracker.awaitSlot(isIdr = false, waitMs = 0))
        assertFalse(tracker.awaitSlot(isIdr = false, waitMs = 0))
        assertEquals(4, tracker.current())

        // Releasing outputs switches tracker to primed mode (limit=2, IDR=3)
        tracker.onOutputReleased(2)
        assertEquals(2, tracker.current())
        assertFalse(tracker.awaitSlot(isIdr = false, waitMs = 0))
        assertTrue(tracker.awaitSlot(isIdr = true, waitMs = 0))
        assertEquals(3, tracker.current())

        tracker.cancelReserved()
        assertEquals(2, tracker.current())
        tracker.reset()
        assertEquals(0, tracker.current())
    }

    @Test
    fun decodeTelemetryWindowComputesPercentilesAndPacketFormat() {
        val win = DecodeTelemetryWindow(windowMs = 500L, startMs = 1000L)
        win.recordDecoded(4.0, inFlightNow = 1)
        win.recordDecoded(8.0, inFlightNow = 2)
        win.recordDecoded(20.0, inFlightNow = 2)
        win.recordDropped()

        // Before 500ms window elapses -> null
        assertEquals(null, win.pollPacket(1400L))

        val pkt = win.pollPacket(1500L)!!
        assertEquals(11, pkt.size)
        assertEquals(0x23, pkt[0].toInt() and 0xFF)
        fun u16(o: Int) = (pkt[o].toInt() and 0xFF) or ((pkt[o + 1].toInt() and 0xFF) shl 8)
        assertEquals(8, u16(1))   // decP50
        assertEquals(20, u16(3))  // decP95
        assertEquals(60, u16(5))  // fpsX10 (3 frames / 0.5s = 6.0 fps -> 60)
        assertEquals(1, u16(7))   // dropped
        assertEquals(2, u16(9))   // maxInFlight

        // Reset after poll -> empty next window returns null
        assertEquals(null, win.pollPacket(2100L))
    }
}
