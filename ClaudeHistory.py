"""Read the current SSH user's local Claude Code transcripts."""
import base64
from collections import deque
import heapq
import json
import os
from pathlib import Path
import sys
import uuid


def token(path):
    return base64.urlsafe_b64encode(path.as_posix().encode()).decode().rstrip("=")


def session_file(path):
    try:
        return path.suffix == ".jsonl" and not path.is_symlink() and path.is_file() and str(uuid.UUID(path.stem)) == path.stem
    except (OSError, ValueError):
        return False


def records(path, max_bytes, tail=False):
    with path.open("rb") as stream:
        size = os.fstat(stream.fileno()).st_size
        if tail and size > max_bytes:
            stream.seek(size - max_bytes - 1)
            stream.readline(max_bytes + 1)
        remaining = min(max_bytes, max(0, size - stream.tell()))
        while remaining:
            capacity = min(remaining, 512 * 1024)
            line = stream.readline(capacity + 1)
            if not line:
                break
            remaining = max(0, remaining - len(line))
            if len(line) > capacity or (not line.endswith(b"\n") and stream.tell() < size):
                while remaining and not line.endswith(b"\n"):
                    fragment = stream.readline(min(remaining, 512 * 1024))
                    remaining -= len(fragment)
                    if not fragment or fragment.endswith(b"\n"):
                        break
                continue
            try:
                record = json.loads(line)
            except (UnicodeError, ValueError, RecursionError):
                continue
            if isinstance(record, dict):
                yield record


def texts(record):
    role = record.get("type")
    if role not in ("user", "assistant") or record.get("isMeta") or record.get("isSidechain"):
        return []
    message = record.get("message")
    if not isinstance(message, dict):
        return []
    content = message.get("content", "")
    if isinstance(content, str):
        content = [{"type": "text", "text": content}]
    if not isinstance(content, list):
        return []
    result = []
    for block in content:
        if not isinstance(block, dict) or block.get("type") != "text":
            continue
        text = block.get("text")
        if isinstance(text, str) and text.strip() and not text.lstrip().startswith((
            "<local-command", "<command-name>", "<command-message>", "<system-reminder>", "<task-notification>"
        )):
            result.append(text)
    return result


def newest(root):
    def candidates():
        for folder in root.iterdir():
            if folder.is_symlink() or not folder.is_dir():
                continue
            try:
                for file in folder.iterdir():
                    if session_file(file):
                        yield file.stat().st_mtime, file
            except OSError:
                continue

    chats = []
    if not root.is_dir():
        return chats
    for time, file in heapq.nlargest(75, candidates(), key=lambda item: item[0]):
        first, title, summary, cwd = "", "", "", ""
        try:
            for entry in records(file, 128 * 1024):
                if not cwd and isinstance(entry.get("cwd"), str):
                    cwd = entry["cwd"]
                if entry.get("type") == "user" and not first:
                    first = " ".join(texts(entry))
            for entry in records(file, 128 * 1024, tail=True):
                if isinstance(entry.get("customTitle"), str):
                    title = entry["customTitle"]
                if isinstance(entry.get("summary"), str):
                    summary = entry["summary"]
                if not first and entry.get("type") == "user":
                    first = " ".join(texts(entry))
        except OSError:
            continue
        if not (first or title or summary):
            continue
        chats.append({
            "key": token(file.relative_to(root)),
            "title": " ".join((title or summary or first).split())[:130] or file.stem[:8],
            "project": Path(cwd).name[:80] if cwd else "",
            "updated": int(time),
        })
        if len(chats) == 50:
            break
    return chats


def history(root, key):
    relative = Path(base64.urlsafe_b64decode(key + "=" * (-len(key) % 4)).decode("utf-8"))
    if relative.is_absolute() or len(relative.parts) != 2 or any(part in (".", "..") for part in relative.parts):
        return {"error": "Invalid chat reference"}
    file = root / relative
    if file.parent.is_symlink() or not session_file(file):
        return {"error": "Chat is no longer available"}
    limit = 8 * 1024 * 1024
    messages = deque(maxlen=70)
    count = 0
    truncated = file.stat().st_size > limit
    for entry in records(file, limit, tail=True):
        blocks = texts(entry)
        if blocks:
            count += 1
            full = "\n\n".join(blocks)
            truncated |= len(full) > 4000
            messages.append({"role": entry["type"], "text": full[:4000]})
    return {"messages": list(messages), "limited": truncated or count > len(messages)}


def main():
    config = Path(os.environ.get("CLAUDE_CONFIG_DIR") or "~/.claude").expanduser()
    projects = config / "projects"
    try:
        result = newest(projects) if sys.argv[1] == "list" else history(projects, sys.argv[2])
    except (OSError, ValueError) as error:
        result = {"error": str(error)}
    print(json.dumps(result, ensure_ascii=True))


if __name__ == "__main__":
    main()
