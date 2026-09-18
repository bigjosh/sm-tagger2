import argparse
import os
import smtplib
import socket
import struct
import time
import uuid

SMTP_HOST = "127.0.0.1"
SMTP_PORT = 25
LOCAL_MAILBOX = "sender@sequence.test"  # Existing test mailbox; SM controls delivery routing.
# SM rejects the IP-literal sender; use a reserved test domain instead.
FROM_ADDRESS = "testing@example.test"
HELO_NAME = "disconnect.example.test"

parser = argparse.ArgumentParser()
parser.add_argument(
    "--when",
    choices=["before-quit", "after-quit"],
    default="before-quit",
)
args = parser.parse_args()

if os.name != "nt":
    raise SystemExit("This version uses Windows socket options.")

subject = f"disconnect-{args.when}-{uuid.uuid4().hex}"
message = (
    f"From: {FROM_ADDRESS}\r\n"
    f"To: {LOCAL_MAILBOX}\r\n"
    f"Subject: {subject}\r\n"
    "\r\n"
    "SMTP disconnect test.\r\n"
)


# Stop immediately if an SMTP command does not receive its expected reply.
def require(reply, allowed=(250,)):
    if reply[0] not in allowed:
        raise smtplib.SMTPResponseException(*reply)


quit_reply = None
smtp = smtplib.SMTP(SMTP_HOST, SMTP_PORT, timeout=15)
try:
    require(smtp.ehlo(HELO_NAME))
    require(smtp.mail(FROM_ADDRESS))
    require(smtp.rcpt(LOCAL_MAILBOX), (250, 251, 252))

    # Configure an abortive TCP close before the timing-sensitive exchange.
    smtp.sock.setsockopt(
        socket.SOL_SOCKET, socket.SO_LINGER, struct.pack("HH", 1, 0)
    )

    data_reply = smtp.data(message)  # Waits for final DATA response.
    require(data_reply)
    time.sleep(5)

    if args.when == "after-quit":
        quit_reply = smtp.docmd("QUIT")  # Wait for SM's reply.
finally:
    smtp.close()  # Closes reader and socket; does not send QUIT.

# Print only after closing, avoiding console delay before the disconnect.
print("Subject:", subject)
print("DATA:", data_reply)
if quit_reply is not None:
    print("QUIT:", quit_reply)
    require(quit_reply, (221,))
print("Abortive close requested:", args.when)
