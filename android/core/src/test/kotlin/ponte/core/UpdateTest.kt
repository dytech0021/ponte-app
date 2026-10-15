package ponte.core

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue
import java.net.ServerSocket
import kotlin.concurrent.thread

class UpdateTest {
    private fun manifest(code: Long = 4, size: Long = 3, hash: String = Crypto.hex(Crypto.sha256(byteArrayOf(1, 2, 3)))) =
        Json.write(mapOf("versionCode" to code, "versionName" to "1.5.1", "size" to size, "sha256" to hash))

    @Test fun soOfereceVersaoMaisNova() {
        assertNull(AndroidUpdate.parse(manifest(3), 3))
        assertNull(AndroidUpdate.parse(manifest(2), 3))
        assertEquals(4, AndroidUpdate.parse(manifest(), 3)!!.versionCode)
    }

    @Test fun rejeitaManifestosInvalidos() {
        for (bad in listOf(manifest(size = 0), manifest(size = 67108865), manifest(hash = "aa"), manifest(code = Long.MAX_VALUE), "{}")) {
            assertFailsWith<IllegalArgumentException> { AndroidUpdate.parse(bad, 3) }
        }
    }

    @Test fun confereTamanhoEResumo() {
        val update = AndroidUpdate.parse(manifest(), 3)!!
        assertTrue(update.matches(byteArrayOf(1, 2, 3)))
        assertFalse(update.matches(byteArrayOf(3, 2, 1)))
        assertFalse(update.matches(byteArrayOf(1, 2)))
    }

    private fun serve(response: ByteArray, check: (UpdateFeed) -> Unit) {
        ServerSocket(0).use { server ->
            val worker = thread {
                server.accept().use { socket ->
                    assertEquals("PONTE ANDROID_MANIFEST 0 teste", socket.getInputStream().bufferedReader().readLine())
                    socket.getOutputStream().write(response)
                }
            }
            check(UpdateFeed("127.0.0.1:${server.localPort}", "teste"))
            worker.join(3000)
            assertFalse(worker.isAlive)
        }
    }

    @Test fun recebeManifestoEmConexaoReal() {
        val body = manifest().toByteArray()
        serve("OK ${body.size}\n".toByteArray() + body) { assertEquals(4, it.check(3)!!.versionCode) }
    }

    @Test fun servidorSemPublicacao() = serve("ERR none\n".toByteArray()) { assertNull(it.check(3)) }

    @Test fun rejeitaRespostaGrandeOuTruncada() {
        for (reply in listOf("OK 9999999\n", "OK 10\n{}", "ERR key\n")) {
            serve(reply.toByteArray()) { assertFailsWith<Exception> { it.check(3) } }
        }
    }
}
