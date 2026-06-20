namespace EdgeKit.Core.Tasks;

public interface ITaskBoardRepository
{
    event EventHandler? Changed;

    IReadOnlyList<TaskCard> GetAll();

    long Add(TaskCard card);

    void Update(TaskCard card);

    void Delete(long id);

    void Move(long id, TaskCardStatus status, int sortOrder);
}