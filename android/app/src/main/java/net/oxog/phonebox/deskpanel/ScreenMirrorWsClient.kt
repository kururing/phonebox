package net.oxog.phonebox.deskpanel

import android.util.Base64
import java.io.BufferedInputStream
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.security.MessageDigest
import java.security.SecureRandom
import javax.net.ssl.SSLSocket

internal class WsFallbackException(message: String) : IOException(message)

internal class WsSessionInactiveException(message: String) : IOException(message)

internal class ScreenMirrorWsClient {
    internal enum class UpgradeAction { OK, FALLBACK, SESSION_INACTIVE, ERROR }

    companion object {
        const val OP_CONT = 0x0
        const val OP_TEXT = 0x1
        const val OP_BINARY = 0x2
        const val OP_CLOSE = 0x8
        const val OP_PING = 0x9
        const val OP_PONG = 0xA
        private const val WS_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"

        internal fun classifyUpgradeStatus(code: Int): UpgradeAction = when (code) {
            101 -> UpgradeAction.OK
            404, 405, 501 -> UpgradeAction.FALLBACK
            409 -> UpgradeAction.SESSION_INACTIVE
            else -> UpgradeAction.ERROR
        }
    }

    class Message {
        var opcode: Int = 0
        var data: ByteArray = ByteArray(512 * 1024)
        var length: Int = 0
        var closeCode: Int = 0
        var closeReason: String = ""
    }

    private var socket: SSLSocket? = null
    private var input: InputStream? = null
    private var output: OutputStream? = null
    private val rng = SecureRandom()
    private val sendHeaderBuf = ByteArray(14)
    private val maskBuf = ByteArray(4)
    private var sendFrameBuf = ByteArray(512)
    @Volatile private var closed = false

    fun connect(link: AgentLink, path: String = "/api/screen/ws"): AgentLink.Route {
        val targets = link.wsTargets()
        if (targets.isEmpty()) throw IOException("No USB or Wi-Fi target available")

        var lastErr: IOException? = null
        for (target in targets) {
            var s: SSLSocket? = null
            try {
                val factory = link.getPinnedFactory(target.pin)
                s = factory.createSocket() as SSLSocket
                s.tcpNoDelay = true
                s.keepAlive = true
                s.setPerformancePreferences(0, 2, 1)
                s.receiveBufferSize = 128 * 1024
                s.sendBufferSize = 64 * 1024
                if (target.route == AgentLink.Route.WIFI) {
                    runCatching { s.trafficClass = 0xB8 }
                }
                val connectTimeout = if (target.route == AgentLink.Route.USB) 500 else 3000
                s.connect(InetSocketAddress(target.host, target.port), connectTimeout)
                s.soTimeout = 4000
                s.startHandshake()

                val bis = BufferedInputStream(s.inputStream, 64 * 1024)
                val out = s.outputStream
                performUpgrade(bis, out, target, path, link.agentHeaders())

                s.soTimeout = 6000
                if (closed) {
                    try { s.close() } catch (_: IOException) {}
                    throw IOException("Closed while connecting")
                }
                socket = s
                input = bis
                output = out
                return target.route
            } catch (fallback: WsFallbackException) {
                try { s?.close() } catch (_: IOException) {}
                throw fallback
            } catch (inactive: WsSessionInactiveException) {
                try { s?.close() } catch (_: IOException) {}
                throw inactive
            } catch (io: IOException) {
                try { s?.close() } catch (_: IOException) {}
                lastErr = io
            }
        }
        throw lastErr ?: IOException("WebSocket connect failed")
    }

    private fun performUpgrade(
        bis: BufferedInputStream,
        out: OutputStream,
        target: AgentLink.WsTarget,
        path: String,
        headers: Map<String, String>,
    ) {
        val keyBytes = ByteArray(16).also { rng.nextBytes(it) }
        val wsKey = Base64.encodeToString(keyBytes, Base64.NO_WRAP)

        // Do not send Sec-WebSocket-Extensions so permessage-deflate stays disabled.
        val sb = StringBuilder(512)
        sb.append("GET ").append(path).append(" HTTP/1.1\r\n")
            .append("Host: ").append(target.host).append(':').append(target.port).append("\r\n")
            .append("Upgrade: websocket\r\n")
            .append("Connection: Upgrade\r\n")
            .append("Sec-WebSocket-Key: ").append(wsKey).append("\r\n")
            .append("Sec-WebSocket-Version: 13\r\n")
            .append("Authorization: Bearer ").append(target.token).append("\r\n")
        for ((k, v) in headers) {
            if (v.isNotEmpty()) sb.append(k).append(": ").append(v).append("\r\n")
        }
        sb.append("\r\n")
        out.write(sb.toString().toByteArray(Charsets.US_ASCII))
        out.flush()

        val statusLine = readLineAscii(bis) ?: throw EOFException("Empty HTTP upgrade response")
        val statusCode = parseStatusCode(statusLine)
        when (classifyUpgradeStatus(statusCode)) {
            UpgradeAction.OK -> {}
            UpgradeAction.FALLBACK -> throw WsFallbackException("Server returned HTTP $statusCode for $path")
            UpgradeAction.SESSION_INACTIVE -> throw WsSessionInactiveException("Screen mirror session inactive (HTTP 409)")
            UpgradeAction.ERROR -> throw IOException("WebSocket upgrade failed: $statusLine")
        }

        val dig = MessageDigest.getInstance("SHA-1").digest((wsKey + WS_GUID).toByteArray(Charsets.US_ASCII))
        val expectedAccept = Base64.encodeToString(dig, Base64.NO_WRAP)

        var gotAccept = false
        while (true) {
            val line = readLineAscii(bis) ?: throw EOFException("EOF in WS headers")
            if (line.isEmpty()) break
            val colon = line.indexOf(':')
            if (colon > 0) {
                val k = line.substring(0, colon).trim()
                val v = line.substring(colon + 1).trim()
                if (k.equals("Sec-WebSocket-Accept", ignoreCase = true) && v == expectedAccept) {
                    gotAccept = true
                }
            }
        }
        if (!gotAccept) throw IOException("Invalid Sec-WebSocket-Accept")
    }

    private fun parseStatusCode(statusLine: String): Int {
        val parts = statusLine.split(' ')
        return if (parts.size >= 2) parts[1].toIntOrNull() ?: -1 else -1
    }

    private fun readLineAscii(bis: InputStream): String? {
        val sb = StringBuilder(128)
        var prev = -1
        while (true) {
            val b = bis.read()
            if (b < 0) return if (sb.isEmpty()) null else sb.toString()
            if (b == '\n'.code) {
                if (prev == '\r'.code && sb.isNotEmpty()) sb.setLength(sb.length - 1)
                return sb.toString()
            }
            sb.append(b.toChar())
            prev = b
        }
    }

    @Synchronized
    fun sendText(text: String) {
        val bytes = text.toByteArray(Charsets.UTF_8)
        sendFrame(OP_TEXT, bytes, 0, bytes.size)
    }

    @Synchronized
    fun sendBinary(data: ByteArray, off: Int = 0, len: Int = data.size) {
        sendFrame(OP_BINARY, data, off, len)
    }

    @Synchronized
    private fun sendFrame(opcode: Int, data: ByteArray, off: Int, len: Int) {
        val o = output ?: throw IOException("WebSocket not connected")
        if (closed) throw IOException("WebSocket closed")

        var hLen = 0
        sendHeaderBuf[hLen++] = (0x80 or (opcode and 0x0F)).toByte()
        if (len <= 125) {
            sendHeaderBuf[hLen++] = (0x80 or len).toByte()
        } else if (len <= 65535) {
            sendHeaderBuf[hLen++] = (0x80 or 126).toByte()
            sendHeaderBuf[hLen++] = ((len ushr 8) and 0xFF).toByte()
            sendHeaderBuf[hLen++] = (len and 0xFF).toByte()
        } else {
            sendHeaderBuf[hLen++] = (0x80 or 127).toByte()
            val l = len.toLong()
            for (i in 7 downTo 0) {
                sendHeaderBuf[hLen++] = ((l ushr (i * 8)) and 0xFFL).toByte()
            }
        }
        rng.nextBytes(maskBuf)
        System.arraycopy(maskBuf, 0, sendHeaderBuf, hLen, 4)
        hLen += 4

        val total = hLen + len
        if (sendFrameBuf.size < total) {
            sendFrameBuf = ByteArray(maxOf(total, sendFrameBuf.size * 2))
        }
        val frame = sendFrameBuf
        System.arraycopy(sendHeaderBuf, 0, frame, 0, hLen)
        for (i in 0 until len) {
            frame[hLen + i] = (data[off + i].toInt() xor maskBuf[i and 3].toInt()).toByte()
        }
        o.write(frame, 0, total)
        o.flush()
    }

    fun readMessage(msg: Message): Boolean {
        return try {
            readMessageInternal(msg)
        } catch (e: IOException) {
            if (closed) false else throw e
        }
    }

    private fun readMessageInternal(msg: Message): Boolean {
        val bis = input ?: return false
        var totalLen = 0
        var msgOpcode = -1

        while (true) {
            val b0 = bis.read()
            if (b0 < 0) return false
            val b1 = bis.read()
            if (b1 < 0) return false

            val fin = (b0 and 0x80) != 0
            val opcode = b0 and 0x0F
            val masked = (b1 and 0x80) != 0
            var payloadLen = (b1 and 0x7F).toLong()

            if (payloadLen == 126L) {
                val hi = readByteOrEof(bis)
                val lo = readByteOrEof(bis)
                payloadLen = (((hi and 0xFF) shl 8) or (lo and 0xFF)).toLong()
            } else if (payloadLen == 127L) {
                var v = 0L
                for (i in 0 until 8) {
                    v = (v shl 8) or (readByteOrEof(bis).toLong() and 0xFFL)
                }
                payloadLen = v
            }

            if (payloadLen < 0 || payloadLen > 32L * 1024L * 1024L) {
                throw IOException("Payload too large: $payloadLen")
            }

            var frameMask: ByteArray? = null
            if (masked) {
                frameMask = ByteArray(4)
                readFully(bis, frameMask, 0, 4)
            }

            val pLen = payloadLen.toInt()
            if (opcode >= 0x8) {
                val ctrl = if (pLen > 0) ByteArray(pLen) else ByteArray(0)
                if (pLen > 0) {
                    readFully(bis, ctrl, 0, pLen)
                    if (frameMask != null) unmask(ctrl, 0, pLen, frameMask)
                }
                if (opcode == OP_CLOSE) {
                    msg.opcode = OP_CLOSE
                    msg.closeCode = if (pLen >= 2) (((ctrl[0].toInt() and 0xFF) shl 8) or (ctrl[1].toInt() and 0xFF)) else 1000
                    msg.closeReason = if (pLen > 2) String(ctrl, 2, pLen - 2, Charsets.UTF_8) else ""
                    return false
                }
                if (opcode == OP_PING) {
                    sendFrame(OP_PONG, ctrl, 0, pLen)
                }
                continue
            }

            if (opcode != OP_CONT) {
                msgOpcode = opcode
                totalLen = 0
            }

            val needed = totalLen + pLen
            if (msg.data.size < needed) {
                msg.data = msg.data.copyOf(maxOf(needed, msg.data.size * 2))
            }
            if (pLen > 0) {
                readFully(bis, msg.data, totalLen, pLen)
                if (frameMask != null) unmask(msg.data, totalLen, pLen, frameMask)
                totalLen += pLen
            }

            if (fin) {
                msg.opcode = msgOpcode
                msg.length = totalLen
                return true
            }
        }
    }

    private fun readByteOrEof(bis: InputStream): Int {
        val b = bis.read()
        if (b < 0) throw EOFException("Unexpected EOF")
        return b
    }

    private fun readFully(bis: InputStream, dst: ByteArray, off: Int, len: Int) {
        var pos = 0
        while (pos < len) {
            val n = bis.read(dst, off + pos, len - pos)
            if (n < 0) throw EOFException("Unexpected EOF")
            pos += n
        }
    }

    private fun unmask(buf: ByteArray, off: Int, len: Int, mask: ByteArray) {
        for (i in 0 until len) {
            buf[off + i] = (buf[off + i].toInt() xor mask[i and 3].toInt()).toByte()
        }
    }

    fun close() {
        closed = true
        try { socket?.close() } catch (_: IOException) {}
    }
}
