package ponte.core

import java.net.InetAddress
import java.net.ServerSocket
import kotlin.concurrent.thread
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/** A bolinha de online: um servidor de retransmissão falso responde o STATUS; IP com porta aberta é online. */
class PresenceTest {
    private fun fakeRelay(reply: (String) -> String?): ServerSocket {
        val server = ServerSocket(0, 8, InetAddress.getByName("127.0.0.1"))
        thread(isDaemon = true) {
            while (true) {
                val client = try { server.accept() } catch (e: java.io.IOException) { break }
                client.use {
                    val line = it.getInputStream().bufferedReader().readLine() ?: ""
                    reply(line)?.let { answer -> it.getOutputStream().write("$answer\n".toByteArray()) }
                }
            }
        }
        return server
    }

    @Test
    fun idPeloServidorEIpPelaPorta() {
        var asked = ""
        val relay = fakeRelay { line -> asked = line; "STATUS 10" }
        val open = ServerSocket(0, 8, InetAddress.getByName("127.0.0.1"))
        val closedPort = ServerSocket(0).use { it.localPort }
        try {
            val targets = listOf("111 222 333", "444555666", "127.0.0.1:${open.localPort}", "127.0.0.1:$closedPort").map { Target.parse(it) }
            val seen = Presence.check("127.0.0.1:${relay.localPort}", "chave_certa", targets, probeMillis = 1000)
            assertEquals("PONTE STATUS 111222333,444555666 chave_certa", asked)
            assertEquals(true, seen["id:111222333"])
            assertEquals(false, seen["id:444555666"])
            assertEquals(true, seen[targets[2].key])
            assertEquals(false, seen[targets[3].key])
        } finally {
            relay.close()
            open.close()
        }
    }

    @Test
    fun servidorAntigoOuForaDoArNaoPintaTudoDeVermelho() {
        // Servidor antigo: não conhece STATUS e só fecha a conexão.
        val old = fakeRelay { null }
        try {
            val seen = Presence.check("127.0.0.1:${old.localPort}", "", listOf(Target.parse("111222333")))
            assertFalse("id:111222333" in seen)
        } finally { old.close() }
        val gone = ServerSocket(0).use { it.localPort }
        assertTrue(Presence.check("127.0.0.1:$gone", "", listOf(Target.parse("111222333"))).isEmpty())
        assertTrue(Presence.check("", "", listOf(Target.parse("111222333"))).isEmpty())
    }
}
