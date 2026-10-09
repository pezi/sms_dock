package app.flutterdev.smsdock;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.io.ByteArrayOutputStream;
import java.io.PrintStream;
import java.lang.reflect.Constructor;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import org.junit.jupiter.api.Test;

/**
 * The programs of {@code examples/}. They are compiled with the tests; they live in no
 * package, so the tests reach them by reflection.
 */
class ExamplesTest {

    private final Object guard;
    private final Method refusal;

    ExamplesTest() throws Exception {
        Class<?> type = Class.forName("AutoReply$ReplyGuard");
        Constructor<?> constructor = type.getDeclaredConstructor(double.class, int.class);
        constructor.setAccessible(true);
        guard = constructor.newInstance(600.0, 3);
        refusal = type.getDeclaredMethod("refusal", String.class, double.class);
        refusal.setAccessible(true);
    }

    private String refusal(String address, double now) throws Exception {
        return (String) refusal.invoke(guard, address, now);
    }

    @Test
    void aNumberGetsOneAnswerInTheCooldown() throws Exception {
        assertNull(refusal("+4369912345678", 0));
        assertEquals("answered a short time ago", refusal("+4369912345678", 1));
        // The same number written differently.
        assertEquals("answered a short time ago", refusal("+43 699 1234-5678", 599));
        assertNull(refusal("+4369912345678", 600));
    }

    @Test
    void sendersThatAreNoPhoneNumbersGetNoAnswer() throws Exception {
        for (String sender : new String[] {"A1", "Post", "8810", "MyBank", "", "+43"}) {
            assertEquals("not a phone number", refusal(sender, 0), sender);
        }
        assertNull(refusal("06991234567", 0));
    }

    @Test
    void atMostSoManyAnswersPerHour() throws Exception {
        assertNull(refusal("+43111111111", 0));
        assertNull(refusal("+43222222222", 1));
        assertNull(refusal("+43333333333", 2));
        assertEquals("too many answers in the last hour", refusal("+43444444444", 3));
        assertEquals("too many answers in the last hour", refusal("+43444444444", 3599));
        assertNull(refusal("+43444444444", 3600));
    }

    @Test
    void answers() throws Exception {
        Method answerFor = Class.forName("AutoReply").getDeclaredMethod("answerFor", String.class);
        answerFor.setAccessible(true);
        assertEquals("All systems running.", answerFor.invoke(null, " status\n"));
        assertEquals("Commands: STATUS", answerFor.invoke(null, "Hello"));
    }

    @Test
    void examplesExplainThemselvesWithoutArguments() throws Exception {
        PrintStream original = System.out;
        try {
            for (String name : new String[] {"ListMessages", "SendSms", "AutoReply"}) {
                ByteArrayOutputStream captured = new ByteArrayOutputStream();
                System.setOut(new PrintStream(captured, true, StandardCharsets.UTF_8));
                Method run = Class.forName(name).getDeclaredMethod("run", String[].class);
                run.setAccessible(true);
                assertEquals(2, run.invoke(null, (Object) new String[0]), name);
                String usage = captured.toString(StandardCharsets.UTF_8);
                assertTrue(usage.contains("examples/" + name + ".java"), usage);
                assertTrue(usage.contains("SMSDOCK_FINGERPRINT"), usage);
            }
        } finally {
            System.setOut(original);
        }
    }
}
