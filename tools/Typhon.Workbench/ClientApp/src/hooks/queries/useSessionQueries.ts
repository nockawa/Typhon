import { usePostApiSessionsFile, useDeleteApiSessionsId } from '@/api/generated/sessions/sessions';

export const useCreateFileSession = () => usePostApiSessionsFile();

export const useDeleteSession = () => useDeleteApiSessionsId();
