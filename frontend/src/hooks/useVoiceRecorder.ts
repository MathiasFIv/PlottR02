import { useCallback, useEffect, useRef, useState } from 'react';

/**
 * Permission lifecycle for the mic: 'unsupported' when the browser lacks the
 * APIs, otherwise 'prompt' (not asked yet) → 'granted' / 'denied'.
 */
export type PermissionState = 'unsupported' | 'prompt' | 'granted' | 'denied';

/** Format preference order — first supported wins, else the browser default. */
const MIME_PREFERENCES = [
  'audio/webm;codecs=opus',
  'audio/webm',
  'audio/mp4',
] as const;

/** Picks the first container/codec the browser's MediaRecorder supports. */
function selectMimeType(): string | undefined {
  if (
    typeof MediaRecorder === 'undefined' ||
    typeof MediaRecorder.isTypeSupported !== 'function'
  ) {
    return undefined;
  }
  for (const candidate of MIME_PREFERENCES) {
    if (MediaRecorder.isTypeSupported(candidate)) return candidate;
  }
  return undefined; // let the browser pick its default
}

/** Best-effort DOMException name extraction for getUserMedia failures. */
function getUserMediaErrorName(err: unknown): string {
  if (err instanceof DOMException) return err.name;
  const name = (err as { name?: unknown } | null)?.name;
  return typeof name === 'string' ? name : '';
}

/**
 * Wraps getUserMedia + MediaRecorder in a small state machine. Records an
 * audio clip into one Blob, exposes a duration timer, and cleans up stream
 * tracks + object URLs so the mic LED never stays on by accident.
 */
export function useVoiceRecorder() {
  const [isRecording, setIsRecording] = useState(false);
  const [durationSeconds, setDurationSeconds] = useState(0);
  const [audioBlob, setAudioBlob] = useState<Blob | null>(null);
  const [audioUrl, setAudioUrl] = useState<string | null>(null);
  const [mimeType, setMimeType] = useState<string | undefined>(selectMimeType);
  const [permissionState, setPermissionState] = useState<PermissionState>(
    () => {
      if (typeof navigator === 'undefined') return 'unsupported';
      if (
        !navigator.mediaDevices?.getUserMedia ||
        typeof MediaRecorder === 'undefined'
      ) {
        return 'unsupported';
      }
      return 'prompt';
    },
  );
  const [error, setError] = useState<string | null>(null);

  const chunkMimeTypeRef = useRef<string | undefined>(selectMimeType());
  const streamRef = useRef<MediaStream | null>(null);
  const recorderRef = useRef<MediaRecorder | null>(null);
  const chunksRef = useRef<Blob[]>([]);
  const intervalRef = useRef<ReturnType<typeof setInterval> | null>(null);
  const urlRef = useRef<string | null>(null);
  /** True while getUserMedia is in flight — blocks double-tap re-entrancy. */
  const startingRef = useRef(false);

  const startRecording = useCallback(async () => {
    if (permissionState === 'unsupported') {
      setError('Voice recording is not supported in this browser.');
      return;
    }
    // Re-entrancy guard: `isRecording` is React state and is not committed
    // while we await getUserMedia, so two rapid taps could otherwise start two
    // concurrent getUserMedia calls and orphan the first stream (mic LED stays
    // on forever). `startingRef` covers that gap — the finally always releases
    // it so the next tap can start a fresh recording.
    if (isRecording || startingRef.current) return;

    startingRef.current = true;
    try {
      setError(null);
      setPermissionState('prompt');

      let stream: MediaStream;
      try {
        stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      } catch (err) {
        const name = getUserMediaErrorName(err);
        if (name === 'NotAllowedError' || name === 'PermissionDeniedError') {
          setPermissionState('denied');
          setError(
            'Microphone permission was denied. Enable it in your browser settings to record voice notes.',
          );
        } else if (name === 'NotFoundError' || name === 'DevicesNotFoundError') {
          setError('No microphone was found on this device.');
        } else if (name === 'NotReadableError') {
          setError('The microphone is in use by another application.');
        } else {
          setError('Could not access the microphone. Try again.');
        }
        return;
      }

      setPermissionState('granted');
      streamRef.current = stream;
      chunksRef.current = [];

      let recorder: MediaRecorder;
      try {
        recorder = chunkMimeTypeRef.current
          ? new MediaRecorder(stream, { mimeType: chunkMimeTypeRef.current })
          : new MediaRecorder(stream);
      } catch {
        // isTypeSupported lied / a browser quirk — retry with the default.
        recorder = new MediaRecorder(stream);
      }
      recorderRef.current = recorder;

      recorder.ondataavailable = (event) => {
        if (event.data && event.data.size > 0) {
          chunksRef.current.push(event.data);
        }
      };

      recorder.onstop = () => {
        const effectiveType =
          recorder.mimeType || chunkMimeTypeRef.current || 'audio/webm';
        const blob = new Blob(chunksRef.current, { type: effectiveType });
        chunksRef.current = [];

        chunkMimeTypeRef.current = effectiveType;
        setMimeType(effectiveType);
        recorderRef.current = null;

        // Revoke any earlier clip's URL before exposing the new one.
        if (urlRef.current) {
          URL.revokeObjectURL(urlRef.current);
        }
        const url = URL.createObjectURL(blob);
        urlRef.current = url;

        setAudioBlob(blob);
        setAudioUrl(url);
        setIsRecording(false);
      };

      recorder.onerror = () => {
        if (intervalRef.current !== null) {
          clearInterval(intervalRef.current);
          intervalRef.current = null;
        }
        recorderRef.current = null;
        // Release the mic too — otherwise the browser keeps the indicator lit.
        streamRef.current?.getTracks().forEach((track) => track.stop());
        streamRef.current = null;
        setIsRecording(false);
        setError('The recorder failed while capturing audio.');
      };

      if (intervalRef.current !== null) {
        clearInterval(intervalRef.current);
      }
      setDurationSeconds(0);
      intervalRef.current = setInterval(() => {
        setDurationSeconds((prev) => prev + 1);
      }, 1000);

      recorder.start();
      setIsRecording(true);
    } finally {
      startingRef.current = false;
    }
  }, [permissionState, isRecording]);

  const stopRecording = useCallback(() => {
    if (intervalRef.current !== null) {
      clearInterval(intervalRef.current);
      intervalRef.current = null;
    }

    const recorder = recorderRef.current;
    if (recorder && recorder.state !== 'inactive') {
      // Finalize the clip first, then release the mic so the LED goes off.
      recorder.stop();
    }

    streamRef.current?.getTracks().forEach((track) => track.stop());
    streamRef.current = null;
    setIsRecording(false);
  }, []);

  const reset = useCallback(() => {
    if (intervalRef.current !== null) {
      clearInterval(intervalRef.current);
      intervalRef.current = null;
    }

    if (recorderRef.current) {
      recorderRef.current.onstop = null;
      recorderRef.current.onerror = null;
      recorderRef.current.ondataavailable = null;
      recorderRef.current = null;
    }

    streamRef.current?.getTracks().forEach((track) => track.stop());
    streamRef.current = null;

    if (urlRef.current) {
      URL.revokeObjectURL(urlRef.current);
      urlRef.current = null;
    }

    chunksRef.current = [];
    setAudioBlob(null);
    setAudioUrl(null);
    setDurationSeconds(0);
    setError(null);
    setIsRecording(false);
  }, []);

  // Unmount cleanup: kill the mic, the timer, and any dangling object URL —
  // handlers are detached first so no setState fires for a dead component.
  useEffect(() => {
    return () => {
      if (intervalRef.current !== null) {
        clearInterval(intervalRef.current);
      }
      streamRef.current?.getTracks().forEach((track) => track.stop());
      streamRef.current = null;
      if (recorderRef.current) {
        recorderRef.current.onstop = null;
        recorderRef.current.onerror = null;
        recorderRef.current.ondataavailable = null;
        recorderRef.current = null;
      }
      if (urlRef.current) {
        URL.revokeObjectURL(urlRef.current);
        urlRef.current = null;
      }
    };
  }, []);

  return {
    isRecording,
    durationSeconds,
    audioBlob,
    audioUrl,
    mimeType,
    permissionState,
    error,
    startRecording,
    stopRecording,
    reset,
  };
}