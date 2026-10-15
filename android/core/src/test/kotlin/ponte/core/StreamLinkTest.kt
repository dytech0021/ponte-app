package ponte.core

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull

class StreamLinkTest {
    private val relay = "filabox3d.com.br:7412"
    private val code = "e7tlSerFPTfyRrKetKT5TNVl"

    @Test
    fun linkOuCodigoViramATransmissao() {
        val expected = StreamLink("filabox3d.com.br", code)
        assertEquals(expected, StreamLink.parse("https://filabox3d.com.br/ponte/assistir/#$code", relay))
        assertEquals(expected, StreamLink.parse("  https://filabox3d.com.br/ponte/assistir#$code \n", relay))
        assertEquals(expected, StreamLink.parse(code, relay))
        assertEquals("https://filabox3d.com.br/ponte/assistir/#$code", expected.url)
    }

    @Test
    fun oQueNaoEhTransmissaoSegueComoEndereco() {
        assertNull(StreamLink.parse("123 456 789", relay))
        assertNull(StreamLink.parse("192.168.0.10", relay))
        assertNull(StreamLink.parse("https://filabox3d.com.br/ponte/#$code", relay))
        assertNull(StreamLink.parse("https://filabox3d.com.br/ponte/assistir/#curto", relay))
        assertNull(StreamLink.parse("http://filabox3d.com.br/ponte/assistir/#$code", relay))
        assertNull(StreamLink.parse("123456789012345678901234", relay))   // só números: não é código
        // Servidor configurado por IP: sem nome não há certificado, então não há transmissão.
        assertNull(StreamLink.parse(code, "177.153.35.193:7412"))
        assertNull(StreamLink.parse(code, ""))
    }

    @Test
    fun linkDeOutroServidorAvisa() {
        val error = assertFailsWith<IllegalArgumentException> { StreamLink.parse("https://outro.exemplo.com/ponte/assistir/#$code", relay) }
        assertEquals("Este link é de outro servidor (outro.exemplo.com); o Ponte só assiste às transmissões de filabox3d.com.br.", error.message)
    }
}
